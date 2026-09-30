using System.Collections.Concurrent;
using Framework.Logging;

namespace HermesProxy.World.Server.AccountSync;

/// <summary>
/// A client save that could not get the directory lock in time. The final file is never written without
/// the lock, so the save goes to <c>&lt;file&gt;.deferred</c> (a name the launcher never touches) and a
/// background thread moves it into place as soon as the lock is free. The newest save always wins: a later
/// save that gets the lock supersedes the deferred one, and a pending deferred save is what
/// <see cref="AccountDataManager.LoadData"/> reads and what makes a restore stand down.
///
/// <para>A deferred file left behind by a process that died is never applied blindly (a choice the player
/// made in the launcher afterwards may be newer): <see cref="ArchiveLeftovers"/> moves it into
/// <c>sync-backups</c>, where the launcher's undo can bring it back. Nothing is lost either way.</para>
/// </summary>
public static class AccountSyncDeferredSaves
{
    public const string Suffix = ".deferred";

    private static readonly ConcurrentDictionary<string, Action?> Pending = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private static readonly object WorkerGate = new();
    private static Thread? _worker;

    /// <summary>Pause between two attempts of the background thread.</summary>
    internal static TimeSpan RetryInterval { get; set; } = TimeSpan.FromMilliseconds(200);

    public static string PathFor(string finalPath) => finalPath + Suffix;

    public static bool IsPending(string finalPath) => Pending.ContainsKey(Path.GetFullPath(finalPath));

    /// <summary>Writes <paramref name="bytes"/> to the deferred file and schedules the move. Call while
    /// holding the directory gate (without the file lock). <paramref name="onApplied"/> runs after the move.</summary>
    public static void Defer(string finalPath, byte[] bytes, Action? onApplied)
    {
        string full = Path.GetFullPath(finalPath);
        string deferred = PathFor(full);
        string temp = deferred + ".tmp";
        try
        {
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, deferred, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
        Pending[full] = onApplied;
        EnsureWorker();
    }

    /// <summary>A newer save reached the final file under the lock: the deferred one is out of date.
    /// Call while holding the directory lock.</summary>
    public static void Supersede(string finalPath)
    {
        string full = Path.GetFullPath(finalPath);
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
            using (var held = AccountSyncDirectoryLock.Enter(directory, wait))
            {
                if (!held.HasFileLock) continue;
                if (!Pending.TryRemove(finalPath, out onApplied)) continue; // superseded meanwhile
                string deferred = PathFor(finalPath);
                if (File.Exists(deferred))
                {
                    try
                    {
                        File.Move(deferred, finalPath, overwrite: true);
                    }
                    catch
                    {
                        Pending.TryAdd(finalPath, onApplied); // still the newest save: keep it pending
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

    /// <summary>Moves deferred files this process did not create (left by a process that died) into
    /// <c>sync-backups</c> as <c>&lt;file&gt;.&lt;unix-ms&gt;.deferred.bak</c>. Never applies them.</summary>
    public static void ArchiveLeftovers(string directory, DateTimeOffset now)
    {
        string[] leftovers;
        try { leftovers = Directory.GetFiles(directory, "*" + Suffix); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
        if (leftovers.Length == 0) return;
        using var held = AccountSyncDirectoryLock.Enter(directory, AccountSyncDirectoryLock.RestoreWait);
        if (!held.HasFileLock) return; // the next world entry tries again
        foreach (string deferred in leftovers)
        {
            string finalPath = deferred[..^Suffix.Length];
            if (IsPending(finalPath) || !File.Exists(deferred)) continue;
            string backups = Path.Combine(directory, "sync-backups");
            Directory.CreateDirectory(backups);
            long ms = now.ToUnixTimeMilliseconds();
            string target;
            do target = Path.Combine(backups, $"{Path.GetFileName(finalPath)}.{ms++}.deferred.bak");
            while (File.Exists(target));
            File.Move(deferred, target);
            Log.Print(LogType.Warn, "account sync: kept an unwritten save from an earlier run in sync-backups");
        }
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
