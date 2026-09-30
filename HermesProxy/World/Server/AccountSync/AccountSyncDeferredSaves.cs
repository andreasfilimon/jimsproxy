using System.Buffers.Binary;
using System.Collections.Concurrent;
using Framework.Logging;

namespace HermesProxy.World.Server.AccountSync;

/// <summary>
/// Ordering and deferral of local AccountData saves.
///
/// <para><b>Order.</b> Every save takes a sequence number before it waits for any lock
/// (<see cref="NextSequence"/>) and may write the final file only if no later save got there first
/// (<see cref="Claim"/>, under the directory gate). So an older save that was held up can never replace
/// the bytes of a newer one, whatever order the threads wake up in.</para>
///
/// <para><b>Deferral.</b> The final file is never written without the lock shared with the launcher. A save
/// that cannot get it in time goes to <c>&lt;file&gt;.deferred</c> (a name the launcher never writes) and a
/// background thread moves it into place once the lock frees, still subject to <see cref="Claim"/>.
/// <see cref="AccountDataManager.LoadData"/> reads a pending one, and a restore stands down while one is
/// pending.</para>
///
/// <para><b>After a crash.</b> A <c>.deferred</c> (or a complete <c>.deferred.tmp</c>) left by a process that
/// died is the player's newest save: at the next world entry <see cref="RecoverLeftovers"/> backs up the
/// current file to <c>sync-backups</c> and puts the leftover in its place. Anything that is not a whole
/// container is kept in <c>sync-backups</c> instead. Nothing is deleted.</para>
/// </summary>
public static class AccountSyncDeferredSaves
{
    public const string Suffix = ".deferred";
    public const string TempSuffix = ".deferred.tmp";

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static readonly ConcurrentDictionary<string, (long Sequence, Action? OnApplied)> Pending = new(PathComparer);
    private static readonly ConcurrentDictionary<string, long> LastWritten = new(PathComparer);
    private static readonly object WorkerGate = new();
    private static Thread? _worker;
    private static long _sequence;

    /// <summary>Pause between two attempts of the background thread.</summary>
    internal static TimeSpan RetryInterval { get; set; } = TimeSpan.FromMilliseconds(200);

    public static string PathFor(string finalPath) => finalPath + Suffix;

    public static bool IsPending(string finalPath) => Pending.ContainsKey(Path.GetFullPath(finalPath));

    /// <summary>Taken by a save before it waits for any lock; later saves get larger numbers.</summary>
    public static long NextSequence() => Interlocked.Increment(ref _sequence);

    /// <summary>Under the directory gate: true if <paramref name="sequence"/> may write the final file, i.e.
    /// no later save has written it. Records the write.</summary>
    public static bool Claim(string finalPath, long sequence)
    {
        string full = Path.GetFullPath(finalPath);
        if (LastWritten.TryGetValue(full, out long last) && last > sequence) return false;
        LastWritten[full] = sequence;
        return true;
    }

    /// <summary>Under the directory gate (without the file lock): writes <paramref name="bytes"/> to the
    /// deferred file and schedules the move, unless a later save already wrote or deferred this file.
    /// <paramref name="onApplied"/> runs after the move.</summary>
    public static void Defer(string finalPath, byte[] bytes, long sequence, Action? onApplied)
    {
        string full = Path.GetFullPath(finalPath);
        if (LastWritten.TryGetValue(full, out long last) && last > sequence) return;
        if (Pending.TryGetValue(full, out var pending) && pending.Sequence > sequence) return;
        string deferred = PathFor(full);
        string temp = full + TempSuffix;
        try
        {
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, deferred, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
        Pending[full] = (sequence, onApplied);
        EnsureWorker();
    }

    /// <summary>Under the directory lock, after a claimed write of the final file: an older deferred save of
    /// the same file is out of date.</summary>
    public static void Supersede(string finalPath, long sequence)
    {
        string full = Path.GetFullPath(finalPath);
        if (Pending.TryGetValue(full, out var pending) && pending.Sequence > sequence) return;
        Pending.TryRemove(full, out _);
        string deferred = PathFor(full);
        if (File.Exists(deferred)) File.Delete(deferred);
    }

    /// <summary>One pass: every pending save whose directory lock is free within <paramref name="wait"/> is
    /// moved into place. Returns how many are still pending.</summary>
    public static int ApplyPending(TimeSpan wait)
    {
        foreach (string finalPath in Pending.Keys.ToArray())
        {
            string directory = Path.GetDirectoryName(finalPath)!;
            Action? onApplied = null;
            bool applied = false;
            using (var held = AccountSyncDirectoryLock.Enter(directory, wait, gateWait: wait))
            {
                if (!held.HasFileLock) continue;
                if (!Pending.TryRemove(finalPath, out var entry)) continue; // superseded meanwhile
                onApplied = entry.OnApplied;
                string deferred = PathFor(finalPath);
                if (!Claim(finalPath, entry.Sequence))
                {
                    if (File.Exists(deferred)) File.Delete(deferred); // a later save is already in place
                    continue;
                }
                if (File.Exists(deferred))
                {
                    try
                    {
                        File.Move(deferred, finalPath, overwrite: true);
                    }
                    catch
                    {
                        Pending.TryAdd(finalPath, entry); // still the newest save: keep it pending
                        throw;
                    }
                    applied = true;
                }
            }
            if (applied)
            {
                Log.Print(LogType.Server, "account sync: a deferred AccountData save was written");
                try { onApplied?.Invoke(); }
                catch (Exception ex) { Log.Print(LogType.Warn, $"account sync: after a deferred save ({ex.GetType().Name})"); }
            }
        }
        return Pending.Count;
    }

    /// <summary>Puts a save left by a process that died (<c>.deferred</c>, or a complete <c>.deferred.tmp</c>,
    /// which is newer) in place of the final file, after backing that file up; keeps anything else in
    /// <c>sync-backups</c>. Files this process is still deferring are left alone.</summary>
    public static void RecoverLeftovers(string directory, DateTimeOffset now)
    {
        string[] found;
        try { found = Directory.GetFiles(directory, "*" + Suffix + "*"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
        var byFile = found
            .Where(f => f.EndsWith(Suffix, StringComparison.Ordinal) || f.EndsWith(TempSuffix, StringComparison.Ordinal))
            .GroupBy(f => f.EndsWith(TempSuffix, StringComparison.Ordinal) ? f[..^TempSuffix.Length] : f[..^Suffix.Length], PathComparer)
            .ToList();
        if (byFile.Count == 0) return;
        using var held = AccountSyncDirectoryLock.Enter(directory, AccountSyncDirectoryLock.RestoreWait,
            gateWait: AccountSyncDirectoryLock.RestoreWait);
        if (!held.HasFileLock) return; // the next world entry tries again
        long ms = now.ToUnixTimeMilliseconds();
        foreach (var group in byFile)
        {
            string finalPath = group.Key;
            if (IsPending(finalPath)) continue;
            // The temp file was being written after the .deferred it would have replaced: prefer it when whole.
            var candidates = group.OrderBy(f => f.EndsWith(TempSuffix, StringComparison.Ordinal) ? 0 : 1).ToList();
            string? winner = candidates.FirstOrDefault(IsWholeContainer);
            if (winner != null)
            {
                if (File.Exists(finalPath)) File.Copy(finalPath, BackupPath(directory, finalPath, ms++, ""));
                File.Move(winner, finalPath, overwrite: true);
                Claim(finalPath, NextSequence());
                Log.Print(LogType.Warn, "account sync: restored a save an earlier run could not write; the file it replaced is in sync-backups");
            }
            foreach (string rest in candidates.Where(f => f != winner && File.Exists(f)))
                File.Move(rest, BackupPath(directory, finalPath, ms++, rest.EndsWith(TempSuffix, StringComparison.Ordinal) ? ".deferred-tmp" : ".deferred"));
        }
    }

    /// <summary>The 36-byte header's compressed length matches the file length: a write that finished.</summary>
    internal static bool IsWholeContainer(string path)
    {
        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            return bytes.Length >= 36 && BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(32)) == bytes.Length - 36;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static string BackupPath(string directory, string finalPath, long ms, string tag)
    {
        string backups = Path.Combine(directory, "sync-backups");
        Directory.CreateDirectory(backups);
        string target;
        do target = Path.Combine(backups, $"{Path.GetFileName(finalPath)}.{ms++}{tag}.bak");
        while (File.Exists(target));
        return target;
    }

    private static void EnsureWorker()
    {
        lock (WorkerGate)
        {
            if (_worker is { IsAlive: true }) return;
            _worker = new Thread(Run) { IsBackground = true, Name = "account-sync deferred saves" };
            _worker.Start();
        }
    }

    private static void Run()
    {
        while (true)
        {
            Thread.Sleep(RetryInterval);
            int left;
            try { left = ApplyPending(TimeSpan.Zero); }
            catch (Exception ex)
            {
                Log.Print(LogType.Warn, $"account sync: deferred save not written yet ({ex.GetType().Name})");
                left = Pending.Count;
            }
            if (left == 0)
            {
                lock (WorkerGate)
                {
                    if (Pending.IsEmpty) { _worker = null; return; }
                }
            }
        }
    }
}
