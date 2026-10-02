using System.Collections.Concurrent;
using System.Net;
using System.Threading.Channels;
using Framework.Logging;

namespace HermesProxy.World.Server.AccountSync;

/// <summary>Fail-open orchestration for binding restore, upload, journaling, and debounce.</summary>
public sealed class AccountSyncService : IDisposable
{
    private static readonly TimeSpan FetchBudget = TimeSpan.FromMilliseconds(1500);
    /// <summary>Each document's own slice of the world-entry budget; a hang cannot starve the others.</summary>
    private static readonly TimeSpan PerDocumentBudget = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(10),
    ];
    private readonly AccountSyncOptions _options;
    private readonly AccountSyncClient _client;
    private readonly IReadOnlyList<AccountSyncDescriptor> _descriptors;
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

    internal Action? RestoreTempWrittenForTest { get; set; }
    internal Action? RestoreLockAcquiredForTest { get; set; }

    /// <summary>Creates a service. The supplied HttpClient is reused for every request.</summary>
    /// <summary>How long a restore waits for the launcher's lock; the world-entry budget cuts it shorter.</summary>
    internal TimeSpan RestoreLockWait { get; set; } = AccountSyncDirectoryLock.RestoreWait;

    public AccountSyncService(AccountSyncOptions options, HttpClient http, IAccountSyncClock? clock = null,
        IReadOnlyList<AccountSyncDescriptor>? descriptors = null)
        : this(options, http, clock, ownsHttp: false, descriptors) { }

    private AccountSyncService(AccountSyncOptions options, HttpClient http, IAccountSyncClock? clock, bool ownsHttp,
        IReadOnlyList<AccountSyncDescriptor>? descriptors = null)
    {
        _options = options;
        _client = new AccountSyncClient(options, http);
        _clock = clock ?? new SystemAccountSyncClock();
        _ownedHttp = ownsHttp ? http : null;
        _descriptors = descriptors ?? AccountSyncDescriptors.All;
        _worker = options.Enabled ? Task.Run(WorkerAsync) : Task.CompletedTask;
    }

    /// <summary>Creates a service over an injected HTTP handler for tests or alternate transports.</summary>
    public AccountSyncService(AccountSyncOptions options, HttpMessageHandler handler, IAccountSyncClock? clock = null,
        IReadOnlyList<AccountSyncDescriptor>? descriptors = null)
        : this(options, new HttpClient(handler, disposeHandler: false), clock, ownsHttp: true, descriptors) { }

    /// <summary>True while configured and not process-disabled by authorization failure.</summary>
    public bool IsEnabled => _options.Enabled && Volatile.Read(ref _disabled) == 0;

    /// <summary>Reconciles types 2 and 3 with one shared 1.5 second network budget and local-write start gate.</summary>
    public void ReconcileAtWorldEntry(string directory, string realmName, WowGuid128 guid)
    {
        if (!IsEnabled) return;
        var firstImplemented = _descriptors.FirstOrDefault(d => d.ImplementedByProxy && d.Type is not null);
        if (firstImplemented is null || AccountSyncKeys.Build(realmName, guid, firstImplemented.Type!.Value, _descriptors) == null)
            return;
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
        string? key = AccountSyncKeys.Build(realmName, guid, type, _descriptors);
        if (key == null) return;
        Queue(NewPendingUpload(directory, realmName, guid, type, key, _clock.UtcNow + Debounce));
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
        // Let the worker hand back what it was doing before the flush looks at the queue.
        try { _worker.Wait(TimeSpan.FromMilliseconds(500)); }
        catch (AggregateException) { }
        FlushPending(budget);
        _pending.Clear();
        Volatile.Write(ref _disabled, 1);
    }

    /// <summary>Tests only: runs right after the worker took an upload off the queue, with the stop token.</summary>
    internal Action<CancellationToken>? WorkerTookItemForTest { get; set; }

    /// <summary>Tests only: stop the background worker the way <see cref="Shutdown"/> does, without the flush.</summary>
    internal void StopWorkerForTest() => _stop.Cancel();

    /// <summary>Tests only: how many uploads are waiting.</summary>
    internal int PendingCountForTest => _pending.Count;

    internal async Task ReconcileUploadForTestAsync(string directory, string realmName, WowGuid128 guid, uint type,
        CancellationToken cancellationToken = default)
    {
        string? key = AccountSyncKeys.Build(realmName, guid, type, _descriptors);
        if (!IsEnabled || key == null) return;
        await ReconcileUploadAsync(NewPendingUpload(directory, realmName, guid, type, key, _clock.UtcNow), cancellationToken);
    }

    internal async Task ReconcileAtWorldEntryAsync(string directory, string realmName, WowGuid128 guid, CancellationToken token)
    {
        var gate = _directoryLocks.GetOrAdd(directory, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(token);
        try
        {
            var capabilities = await _client.GetCapabilitiesAsync(token) ?? throw new InvalidDataException("missing capabilities");
            // One pass over the proxy-implemented descriptors, each with its own switch family. In the
            // shipped table that is bindings only; a test table can turn a family on (N1.2).
            var enabled = _descriptors
                .Where(d => d.ImplementedByProxy && d.Type is not null)
                .Where(d => capabilities.GetMode(d.SwitchFamily) != AccountSyncMode.Off)
                .ToArray();
            if (enabled.Length == 0) return;
            var heads = await _client.ListDocumentsAsync(token) ?? throw new InvalidDataException("missing document list");
            var remoteByKey = heads.Where(h => !string.IsNullOrEmpty(h.Key) && !string.IsNullOrEmpty(h.Revision))
                .ToDictionary(h => h.Key, StringComparer.Ordinal);
            string journalPath = Path.Combine(directory, "sync-journal.json");
            var journal = AccountSyncJournal.Load(journalPath);

            // bindings first, then the rest by lastAttemptAt ascending: a document that ran out of its
            // own sub-budget moves to the back on the next world entry (plan §5 P2.4).
            var ordered = enabled
                .Select(d => (Descriptor: d, Key: AccountSyncKeys.Build(realmName, guid, d.Type!.Value, _descriptors)!))
                .OrderByDescending(x => x.Descriptor.SwitchFamily == "bindings")
                .ThenBy(x => journal.Attempts.TryGetValue(x.Key, out long attemptedAt) ? attemptedAt : 0)
                .ToArray();

            foreach (var (descriptor, key) in ordered)
            {
                if (token.IsCancellationRequested) break;
                uint type = descriptor.Type!.Value;
                AccountSyncMode mode = capabilities.GetMode(descriptor.SwitchFamily);
                // Mark the attempt before the network, so a timeout still moves this document back next time.
                MarkAttempt(journal, key);
                using var perDocument = CancellationTokenSource.CreateLinkedTokenSource(token);
                perDocument.CancelAfter(PerDocumentBudget);
                try
                {
                    string file = GetFilePath(directory, guid, descriptor);
                    byte[]? local = File.Exists(file) ? await File.ReadAllBytesAsync(file, perDocument.Token) : null;
                    string? localHash = local == null ? null : AccountSyncContainer.Sha256(local);
                    journal.Documents.TryGetValue(key, out var journalEntry);
                    remoteByKey.TryGetValue(key, out var remote);
                    var action = AccountSyncDecision.Decide(localHash, journalEntry, remote, mode);

                    switch (action)
                    {
                        case AccountSyncAction.Restore:
                            await RestoreAsync(file, directory, guid, descriptor, key, localHash, remote!, journal, journalPath, perDocument.Token);
                            break;
                        case AccountSyncAction.Adopt:
                            Adopt(journal, journalPath, key, remote!, token: perDocument.Token);
                            break;
                        case AccountSyncAction.UploadCreate:
                        case AccountSyncAction.UploadMatch:
                        case AccountSyncAction.ConflictCandidate:
                            Queue(NewPendingUpload(directory, realmName, guid, type, key, _clock.UtcNow));
                            break;
                        case AccountSyncAction.ConflictBlocked:
                            Log.Print(LogType.Warn, "account sync: remote document conflict awaits launcher resolution");
                            break;
                    }
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    // This document used its own 400 ms sub-budget; the others keep their turn.
                }
                finally
                {
                    // Persist the attempt even after a timeout or failure.
                    TryPersistJournal(journal, journalPath, token);
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Marks a document as attempted-now. The attempt lives in <see cref="AccountSyncJournal.Attempts"/>,
    /// never in <see cref="AccountSyncJournal.Documents"/>: the decision table must keep seeing "no confirmed
    /// revision" until the server actually confirmed one (K2 B1).</summary>
    private void MarkAttempt(AccountSyncJournal journal, string key)
    {
        journal.Attempts[key] = _clock.UtcNow.ToUnixTimeMilliseconds();
    }

    private static void TryPersistJournal(AccountSyncJournal journal, string journalPath, CancellationToken token)
    {
        try { journal.SaveWithinBudget(journalPath, token); }
        catch (OperationCanceledException) { }
        catch (IOException) { }
    }

    private async Task RestoreAsync(string file, string directory, WowGuid128 guid, AccountSyncDescriptor descriptor, string key, string? expectedLocalHash,
        AccountSyncDocumentHead remote, AccountSyncJournal journal, string journalPath, CancellationToken token)
    {
        var document = await _client.GetDocumentAsync(key, token) ?? throw new InvalidDataException("document disappeared");
        byte[] payload;
        try { payload = Convert.FromBase64String(document.PayloadB64); }
        catch (FormatException) { throw new InvalidDataException("invalid payload encoding"); }
        if (document.Revision != remote.Revision || payload.Length != document.ByteLength || payload.Length != remote.ByteLength ||
            !string.Equals(AccountSyncContainer.Sha256(payload), remote.Sha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(document.Sha256, remote.Sha256, StringComparison.OrdinalIgnoreCase) ||
            !AccountSyncContainer.Validate(payload, descriptor.Type!.Value, guid, descriptor.IsCharacter, descriptor.MaxBytes))
            throw new InvalidDataException("invalid remote payload");

        string temp = file + ".sync.tmp";
        try
        {
            token.ThrowIfCancellationRequested();
            File.WriteAllBytes(temp, payload);
            RestoreTempWrittenForTest?.Invoke();
            using (var held = AccountSyncDirectoryLock.Enter(directory, RestoreLockWait, token, gateWait: RestoreLockWait))
            {
                RestoreLockAcquiredForTest?.Invoke();
                // The launcher is replacing this file right now: leave it, the next world entry decides again.
                if (!held.HasFileLock) return;
                // A save of this process is waiting for the lock: it is newer than anything the server has.
                if (AccountSyncDeferredSaves.IsPending(file)) return;
                token.ThrowIfCancellationRequested();
                if (!LocalMatches(file, expectedLocalHash)) return;
                if (expectedLocalHash != null)
                    Backup(file, directory, token);
                token.ThrowIfCancellationRequested();
                File.Move(temp, file, true);
            }
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
        token.ThrowIfCancellationRequested();
        UpdateEntry(journal, key, remote.Revision, remote.Sha256);
        journal.SaveWithinBudget(journalPath, token);
    }

    private void Backup(string file, string directory, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string backupDirectory = Path.Combine(directory, "sync-backups");
        Directory.CreateDirectory(backupDirectory);
        string stem = Path.GetFileName(file) + ".";
        string backup = Path.Combine(backupDirectory, stem + _clock.UtcNow.ToUnixTimeMilliseconds() + ".bak");
        token.ThrowIfCancellationRequested();
        File.Copy(file, backup, true);
        foreach (string old in Directory.GetFiles(backupDirectory, stem + "*.bak").OrderByDescending(path => path, StringComparer.Ordinal).Skip(10))
        {
            token.ThrowIfCancellationRequested();
            File.Delete(old);
        }
    }

    private static bool LocalMatches(string file, string? expectedHash)
    {
        try
        {
            byte[] current = File.ReadAllBytes(file);
            return string.Equals(AccountSyncContainer.Sha256(current), expectedHash, StringComparison.OrdinalIgnoreCase);
        }
        catch (FileNotFoundException)
        {
            return expectedHash == null;
        }
        catch (DirectoryNotFoundException)
        {
            return expectedHash == null;
        }
    }

    private async Task ReconcileUploadAsync(PendingUpload pending, CancellationToken outerToken)
    {
        if (!IsEnabled) return;
        var gate = _directoryLocks.GetOrAdd(pending.Directory, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(outerToken);
        try
        {
            var descriptor = AccountSyncDescriptors.ForType(pending.Type, _descriptors);
            if (descriptor is null || !descriptor.ImplementedByProxy) return;
            string file = GetFilePath(pending.Directory, pending.Guid, descriptor);
            if (!File.Exists(file)) return;
            byte[] payload = await File.ReadAllBytesAsync(file, outerToken);
            if (!AccountSyncContainer.Validate(payload, pending.Type, pending.Guid, descriptor.IsCharacter, descriptor.MaxBytes)) return;
            string hash = AccountSyncContainer.Sha256(payload);

            var capabilities = await _client.GetCapabilitiesAsync(outerToken);
            AccountSyncMode mode = capabilities?.GetMode(descriptor.SwitchFamily) ?? AccountSyncMode.Off;
            if (mode == AccountSyncMode.Off) return;
            var heads = await _client.ListDocumentsAsync(outerToken) ?? [];
            var remote = heads.FirstOrDefault(h => h.Key == pending.Key && !string.IsNullOrEmpty(h.Revision));
            string journalPath = Path.Combine(pending.Directory, "sync-journal.json");
            var journal = AccountSyncJournal.Load(journalPath);
            journal.Documents.TryGetValue(pending.Key, out var journalEntry);
            var action = AccountSyncDecision.Decide(hash, journalEntry, remote, mode);

            if (action == AccountSyncAction.Adopt)
            {
                Adopt(journal, journalPath, pending.Key, remote!, token: outerToken);
                return;
            }
            if (action == AccountSyncAction.ConflictBlocked || action == AccountSyncAction.Nothing || action == AccountSyncAction.Restore)
                return;
            if (action == AccountSyncAction.ConflictCandidate)
            {
                var conflictResult = await _client.PostConflictCandidateAsync(
                    pending.Key, payload, hash, pending.ClientUploadId, outerToken);
                HandleUploadStatus(pending, conflictResult.StatusCode);
                return;
            }

            string? precondition = action == AccountSyncAction.UploadMatch ? journalEntry!.Revision : null;
            var result = await _client.PutDocumentAsync(
                pending.Key, payload, hash, precondition, pending.ClientUploadId, outerToken);
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
            if (IsTransientStatus(result.StatusCode))
            {
                QueueRetry(pending);
                return;
            }
            if (result.StatusCode == HttpStatusCode.OK && result.Head != null)
                Adopt(journal, journalPath, pending.Key, result.Head, hash, outerToken);
        }
        catch (Exception ex)
        {
            HandleAuthorization(ex);
            if (!outerToken.IsCancellationRequested && IsTransient(ex))
                QueueRetry(pending);
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
            Adopt(journal, journalPath, pending.Key, head, hash, token);
            return;
        }
        var result = await _client.PostConflictCandidateAsync(
            pending.Key, payload, hash, pending.ClientUploadId, token);
        HandleUploadStatus(pending, result.StatusCode);
    }

    private static void Adopt(AccountSyncJournal journal, string path, string key, AccountSyncDocumentHead head,
        string? hash = null, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        UpdateEntry(journal, key, head.Revision, hash ?? head.Sha256);
        journal.SaveWithinBudget(path, token);
    }

    /// <summary>Writes the confirmed revision/hash for a key. The attempt timestamp stays in
    /// <see cref="AccountSyncJournal.Attempts"/>, untouched by this.</summary>
    private static void UpdateEntry(AccountSyncJournal journal, string key, string revision, string sha256)
    {
        journal.Documents[key] = new AccountSyncJournalEntry
        {
            Revision = revision,
            Sha256 = sha256,
        };
    }

    private void Queue(PendingUpload pending)
    {
        string identity = pending.Directory + "\n" + pending.Key;
        _pending[identity] = pending;
        _signals.Writer.TryWrite(identity);
    }

    private static PendingUpload NewPendingUpload(string directory, string realmName, WowGuid128 guid, uint type,
        string key, DateTimeOffset due) => new(
        directory, realmName, guid, type, key, due, 0, System.Guid.NewGuid().ToString("D"));

    private void QueueRetry(PendingUpload pending)
    {
        if (pending.RetryAttempt >= RetryDelays.Length || !IsEnabled) return;
        var retry = pending with
        {
            Due = _clock.UtcNow + RetryDelays[pending.RetryAttempt],
            RetryAttempt = pending.RetryAttempt + 1,
        };
        string identity = retry.Directory + "\n" + retry.Key;
        if (_pending.TryAdd(identity, retry))
            _signals.Writer.TryWrite(identity);
    }

    private void HandleUploadStatus(PendingUpload pending, HttpStatusCode statusCode)
    {
        if (statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            DisableForProcess();
        else if (IsTransientStatus(statusCode))
            QueueRetry(pending);
    }

    private static bool IsTransientStatus(HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.TooManyRequests || (int)statusCode >= 500;

    private static bool IsTransient(Exception exception) => exception is HttpRequestException or HttpIOException ||
        exception is OperationCanceledException ||
        exception is AccountSyncHttpException http && IsTransientStatus(http.StatusCode);

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
                    // Shutdown cancels the delay, which WhenAny reports as done: leave the work to the shutdown
                    // flush instead of taking it and running it with a token that is already cancelled.
                    if (_stop.IsCancellationRequested) return;
                    if (_pending.TryRemove(next.Key, out var pending))
                    {
                        WorkerTookItemForTest?.Invoke(_stop.Token);
                        bool finished = false;
                        try
                        {
                            await ReconcileUploadAsync(pending, _stop.Token);
                            finished = !_stop.IsCancellationRequested;
                        }
                        finally
                        {
                            // Stopped while this upload ran: hand it back to the shutdown flush, which waits for
                            // this worker first. Sending it twice is harmless, the server keys it by clientUploadId.
                            if (!finished && _stop.IsCancellationRequested) _pending.TryAdd(next.Key, pending);
                        }
                        while (_signals.Reader.TryRead(out _)) { }
                    }
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

    private static string GetFilePath(string directory, WowGuid128 guid, AccountSyncDescriptor descriptor) =>
        Path.Combine(directory, descriptor.FileNameFor(guid.GetLowValue(), guid.GetHighValue()));

    /// <inheritdoc />
    public void Dispose()
    {
        _stop.Cancel();
        try { _worker.Wait(TimeSpan.FromSeconds(1)); } catch { }
        _stop.Dispose();
        _ownedHttp?.Dispose();
    }

    private sealed record PendingUpload(string Directory, string RealmName, WowGuid128 Guid, uint Type, string Key,
        DateTimeOffset Due, int RetryAttempt, string ClientUploadId);
}
