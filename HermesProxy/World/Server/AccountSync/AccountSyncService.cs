using System.Collections.Concurrent;
using System.Net;
using System.Threading.Channels;
using Framework.Logging;

namespace HermesProxy.World.Server.AccountSync;

/// <summary>Fail-open orchestration for binding restore, upload, journaling, and debounce.</summary>
public sealed class AccountSyncService : IDisposable
{
    private static readonly TimeSpan FetchBudget = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(5);
    private readonly AccountSyncOptions _options;
    private readonly AccountSyncClient _client;
    private readonly IAccountSyncClock _clock;
    private readonly Channel<string> _signals = Channel.CreateBounded<string>(new BoundedChannelOptions(1)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.DropWrite,
    });
    private readonly ConcurrentDictionary<string, PendingUpload> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _directoryLocks = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    private readonly HttpClient? _ownedHttp;
    private int _disabled;
    private int _shutdown;

    /// <summary>Creates a service. The supplied HttpClient is reused for every request.</summary>
    public AccountSyncService(AccountSyncOptions options, HttpClient http, IAccountSyncClock? clock = null)
        : this(options, http, clock, ownsHttp: false) { }

    private AccountSyncService(AccountSyncOptions options, HttpClient http, IAccountSyncClock? clock, bool ownsHttp)
    {
        _options = options;
        _client = new AccountSyncClient(options, http);
        _clock = clock ?? new SystemAccountSyncClock();
        _ownedHttp = ownsHttp ? http : null;
        _worker = options.Enabled ? Task.Run(WorkerAsync) : Task.CompletedTask;
    }

    /// <summary>Creates a service over an injected HTTP handler for tests or alternate transports.</summary>
    public AccountSyncService(AccountSyncOptions options, HttpMessageHandler handler, IAccountSyncClock? clock = null)
        : this(options, new HttpClient(handler, disposeHandler: false), clock, ownsHttp: true) { }

    /// <summary>True while configured and not process-disabled by authorization failure.</summary>
    public bool IsEnabled => _options.Enabled && Volatile.Read(ref _disabled) == 0;

    /// <summary>Reconciles types 2 and 3 within one shared 1.5 second world-entry budget.</summary>
    public void ReconcileAtWorldEntry(string directory, string realmName, WowGuid128 guid)
    {
        if (!IsEnabled || AccountSyncKeys.Build(realmName, guid, 2) == null) return;
        using var budget = new CancellationTokenSource(FetchBudget);
        try
        {
            ReconcileAtWorldEntryAsync(directory, realmName, guid, budget.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            Log.Print(LogType.Warn, "account sync: world-entry budget exceeded; using local data");
        }
        catch (Exception ex)
        {
            HandleAuthorization(ex);
            Log.Print(LogType.Warn, $"account sync: world-entry sync failed ({ex.GetType().Name}); using local data");
        }
    }

    /// <summary>Queues a saved type 2/3 file without blocking its packet handler.</summary>
    public void EnqueueSaved(string directory, string realmName, WowGuid128 guid, uint type)
    {
        if (!IsEnabled) return;
        string? key = AccountSyncKeys.Build(realmName, guid, type);
        if (key == null) return;
        Queue(new PendingUpload(directory, realmName, guid, type, key, _clock.UtcNow + Debounce));
    }

    /// <summary>Flushes currently pending uploads within the caller's total budget.</summary>
    public void FlushPending(TimeSpan budget)
    {
        if (!_options.Enabled || Volatile.Read(ref _disabled) != 0 || _pending.IsEmpty) return;
        using var cts = new CancellationTokenSource(budget);
        try
        {
            foreach (var pair in _pending.ToArray())
            {
                if (!_pending.TryRemove(pair.Key, out var pending)) continue;
                ReconcileUploadAsync(pending, cts.Token).GetAwaiter().GetResult();
            }
        }
        catch { }
    }

    /// <summary>Stops the worker, flushes for a bounded interval, and drops remaining work.</summary>
    public void Shutdown(TimeSpan budget)
    {
        if (Interlocked.Exchange(ref _shutdown, 1) != 0) return;
        _stop.Cancel();
        FlushPending(budget);
        _pending.Clear();
        Volatile.Write(ref _disabled, 1);
    }

    internal async Task ReconcileUploadForTestAsync(string directory, string realmName, WowGuid128 guid, uint type,
        CancellationToken cancellationToken = default)
    {
        string? key = AccountSyncKeys.Build(realmName, guid, type);
        if (!IsEnabled || key == null) return;
        await ReconcileUploadAsync(new PendingUpload(directory, realmName, guid, type, key, _clock.UtcNow), cancellationToken);
    }

    private async Task ReconcileAtWorldEntryAsync(string directory, string realmName, WowGuid128 guid, CancellationToken token)
    {
        var gate = _directoryLocks.GetOrAdd(directory, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(token);
        try
        {
            var capabilities = await _client.GetCapabilitiesAsync(token) ?? throw new InvalidDataException("missing capabilities");
            AccountSyncMode mode = capabilities.GetBindingsMode();
            if (mode == AccountSyncMode.Off) return;
            var heads = await _client.ListDocumentsAsync(token) ?? throw new InvalidDataException("missing document list");
            var remoteByKey = heads.Where(h => !string.IsNullOrEmpty(h.Key) && !string.IsNullOrEmpty(h.Revision))
                .ToDictionary(h => h.Key, StringComparer.Ordinal);
            string journalPath = Path.Combine(directory, "sync-journal.json");
            var journal = AccountSyncJournal.Load(journalPath);

            foreach (uint type in new uint[] { 2, 3 })
            {
                string key = AccountSyncKeys.Build(realmName, guid, type)!;
                string file = GetFilePath(directory, guid, type);
                byte[]? local = File.Exists(file) ? await File.ReadAllBytesAsync(file, token) : null;
                string? localHash = local == null ? null : AccountSyncContainer.Sha256(local);
                journal.Documents.TryGetValue(key, out var journalEntry);
                remoteByKey.TryGetValue(key, out var remote);
                var action = AccountSyncDecision.Decide(localHash, journalEntry, remote, mode);

                switch (action)
                {
                    case AccountSyncAction.Restore:
                        await RestoreAsync(file, directory, guid, type, key, remote!, journal, journalPath, token);
                        break;
                    case AccountSyncAction.Adopt:
                        Adopt(journal, journalPath, key, remote!);
                        break;
                    case AccountSyncAction.UploadCreate:
                    case AccountSyncAction.UploadMatch:
                    case AccountSyncAction.ConflictCandidate:
                        Queue(new PendingUpload(directory, realmName, guid, type, key, _clock.UtcNow));
                        break;
                    case AccountSyncAction.ConflictBlocked:
                        Log.Print(LogType.Warn, "account sync: remote binding conflict awaits launcher resolution");
                        break;
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task RestoreAsync(string file, string directory, WowGuid128 guid, uint type, string key,
        AccountSyncDocumentHead remote, AccountSyncJournal journal, string journalPath, CancellationToken token)
    {
        var document = await _client.GetDocumentAsync(key, token) ?? throw new InvalidDataException("document disappeared");
        byte[] payload;
        try { payload = Convert.FromBase64String(document.PayloadB64); }
        catch (FormatException) { throw new InvalidDataException("invalid payload encoding"); }
        if (document.Revision != remote.Revision || payload.Length != document.ByteLength || payload.Length != remote.ByteLength ||
            !string.Equals(AccountSyncContainer.Sha256(payload), remote.Sha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(document.Sha256, remote.Sha256, StringComparison.OrdinalIgnoreCase) ||
            !AccountSyncContainer.Validate(payload, type, guid))
            throw new InvalidDataException("invalid remote payload");

        if (File.Exists(file))
            Backup(file, directory);
        string temp = file + ".sync.tmp";
        try
        {
            await File.WriteAllBytesAsync(temp, payload, token);
            File.Move(temp, file, true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
        journal.Documents[key] = new() { Revision = remote.Revision, Sha256 = remote.Sha256 };
        journal.Save(journalPath);
    }

    private void Backup(string file, string directory)
    {
        string backupDirectory = Path.Combine(directory, "sync-backups");
        Directory.CreateDirectory(backupDirectory);
        string stem = Path.GetFileName(file) + ".";
        string backup = Path.Combine(backupDirectory, stem + _clock.UtcNow.ToUnixTimeMilliseconds() + ".bak");
        File.Copy(file, backup, true);
        foreach (string old in Directory.GetFiles(backupDirectory, stem + "*.bak").OrderByDescending(path => path, StringComparer.Ordinal).Skip(10))
            File.Delete(old);
    }

    private async Task ReconcileUploadAsync(PendingUpload pending, CancellationToken outerToken)
    {
        if (!IsEnabled) return;
        var gate = _directoryLocks.GetOrAdd(pending.Directory, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(outerToken);
        try
        {
            string file = GetFilePath(pending.Directory, pending.Guid, pending.Type);
            if (!File.Exists(file)) return;
            byte[] payload = await File.ReadAllBytesAsync(file, outerToken);
            if (!AccountSyncContainer.Validate(payload, pending.Type, pending.Guid)) return;
            string hash = AccountSyncContainer.Sha256(payload);

            var capabilities = await _client.GetCapabilitiesAsync(outerToken);
            AccountSyncMode mode = capabilities?.GetBindingsMode() ?? AccountSyncMode.Off;
            if (mode == AccountSyncMode.Off) return;
            var heads = await _client.ListDocumentsAsync(outerToken) ?? [];
            var remote = heads.FirstOrDefault(h => h.Key == pending.Key && !string.IsNullOrEmpty(h.Revision));
            string journalPath = Path.Combine(pending.Directory, "sync-journal.json");
            var journal = AccountSyncJournal.Load(journalPath);
            journal.Documents.TryGetValue(pending.Key, out var journalEntry);
            var action = AccountSyncDecision.Decide(hash, journalEntry, remote, mode);

            if (action == AccountSyncAction.Adopt)
            {
                Adopt(journal, journalPath, pending.Key, remote!);
                return;
            }
            if (action == AccountSyncAction.ConflictBlocked || action == AccountSyncAction.Nothing || action == AccountSyncAction.Restore)
                return;
            if (action == AccountSyncAction.ConflictCandidate)
            {
                var conflictResult = await _client.PostConflictCandidateAsync(pending.Key, payload, hash, outerToken);
                DisableIfAuthorizationRejected(conflictResult.StatusCode);
                return;
            }

            string? precondition = action == AccountSyncAction.UploadMatch ? journalEntry!.Revision : null;
            var result = await _client.PutDocumentAsync(pending.Key, payload, hash, precondition, outerToken);
            if (result.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                DisableForProcess();
                return;
            }
            if (result.StatusCode == HttpStatusCode.Conflict)
            {
                await HandleUploadConflictAsync(pending, payload, hash, journal, journalPath, outerToken);
                return;
            }
            if (result.StatusCode == HttpStatusCode.OK && result.Head != null)
                Adopt(journal, journalPath, pending.Key, result.Head, hash);
        }
        catch (Exception ex)
        {
            HandleAuthorization(ex);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task HandleUploadConflictAsync(PendingUpload pending, byte[] payload, string hash,
        AccountSyncJournal journal, string journalPath, CancellationToken token)
    {
        var heads = await _client.ListDocumentsAsync(token) ?? [];
        var head = heads.FirstOrDefault(h => h.Key == pending.Key && !string.IsNullOrEmpty(h.Revision));
        if (head != null && string.Equals(head.Sha256, hash, StringComparison.OrdinalIgnoreCase))
        {
            Adopt(journal, journalPath, pending.Key, head, hash);
            return;
        }
        var result = await _client.PostConflictCandidateAsync(pending.Key, payload, hash, token);
        DisableIfAuthorizationRejected(result.StatusCode);
    }

    private static void Adopt(AccountSyncJournal journal, string path, string key, AccountSyncDocumentHead head, string? hash = null)
    {
        journal.Documents[key] = new() { Revision = head.Revision, Sha256 = hash ?? head.Sha256 };
        journal.Save(path);
    }

    private void Queue(PendingUpload pending)
    {
        string identity = pending.Directory + "\n" + pending.Key;
        _pending[identity] = pending;
        _signals.Writer.TryWrite(identity);
    }

    private async Task WorkerAsync()
    {
        try
        {
            while (await _signals.Reader.WaitToReadAsync(_stop.Token))
            {
                while (_signals.Reader.TryRead(out _)) { }
                while (!_pending.IsEmpty)
                {
                    var next = _pending.OrderBy(x => x.Value.Due).First();
                    TimeSpan delay = next.Value.Due - _clock.UtcNow;
                    if (delay > TimeSpan.Zero)
                    {
                        var delayTask = _clock.DelayAsync(delay, _stop.Token);
                        var signalTask = _signals.Reader.WaitToReadAsync(_stop.Token).AsTask();
                        if (await Task.WhenAny(delayTask, signalTask) == signalTask) break;
                    }
                    if (_pending.TryRemove(next.Key, out var pending))
                        await ReconcileUploadAsync(pending, _stop.Token);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private void HandleAuthorization(Exception exception)
    {
        if (exception is AccountSyncHttpException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden })
            DisableForProcess();
    }

    private void DisableForProcess()
    {
        if (Interlocked.Exchange(ref _disabled, 1) == 0)
            Log.Print(LogType.Warn, "account sync: off for this process (authorization rejected)");
    }

    private void DisableIfAuthorizationRejected(HttpStatusCode statusCode)
    {
        if (statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            DisableForProcess();
    }

    private static string GetFilePath(string directory, WowGuid128 guid, uint type) => type == 2
        ? Path.Combine(directory, "data-2.bin")
        : Path.Combine(directory, $"data-3-{guid.GetLowValue()}-{guid.GetHighValue()}.bin");

    /// <inheritdoc />
    public void Dispose()
    {
        _stop.Cancel();
        try { _worker.Wait(TimeSpan.FromSeconds(1)); } catch { }
        _stop.Dispose();
        _ownedHttp?.Dispose();
    }

    private sealed record PendingUpload(string Directory, string RealmName, WowGuid128 Guid, uint Type, string Key, DateTimeOffset Due);
}
