using System.Collections.Concurrent;
using System.Diagnostics;

namespace HermesProxy.World.Server.AccountSync;

/// <summary>
/// Serializes every local write in one AccountData directory: within this process through a shared
/// gate, and against the Stonetavern launcher through the lock file <see cref="FileName"/> in the same
/// directory. Both sides open that file with <see cref="FileShare.None"/> (a share mode on Windows, a
/// <c>flock</c> on Linux and macOS) and hold it only for the final check and move. The operating system
/// drops the lock when a process dies; the file itself is never deleted, so both sides always lock the
/// same file. Contract: docs/ACCOUNT-SYNC.md, section "Lock shared with the launcher".
/// </summary>
public static class AccountSyncDirectoryLock
{
    /// <summary>Name of the lock file; the launcher uses the same name. Change both together.</summary>
    public const string FileName = ".st-sync.lock";

    /// <summary>How long a client save waits for the launcher. The launcher holds the lock for milliseconds.</summary>
    public static readonly TimeSpan SaveWait = TimeSpan.FromSeconds(5);

    /// <summary>How long a restore waits before it gives up and leaves the file for the next world entry.</summary>
    public static readonly TimeSpan RestoreWait = TimeSpan.FromSeconds(2);

    private static readonly ConcurrentDictionary<string, object> Gates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    /// <summary>Returns the shared in-process gate for all local writes in the directory.</summary>
    public static object For(string directory) => Gates.GetOrAdd(Normalize(directory), static _ => new object());

    /// <summary>Takes the in-process gate, then tries for the lock file for up to <paramref name="wait"/>, or
    /// until <paramref name="token"/> is cancelled, whichever comes first (a restore must stay inside its
    /// world-entry budget). The gate is always held on return; <see cref="Held.HasFileLock"/> says whether
    /// the file lock is too. Dispose on the same thread.</summary>
    public static Held Enter(string directory, TimeSpan wait, CancellationToken token = default)
    {
        object gate = For(directory);
        Monitor.Enter(gate);
        try
        {
            return new Held(gate, TryOpen(Path.Combine(Normalize(directory), FileName), wait, token));
        }
        catch
        {
            Monitor.Exit(gate);
            throw;
        }
    }

    private static string Normalize(string directory) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));

    private static FileStream? TryOpen(string path, TimeSpan wait, CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (clock.Elapsed < wait && !token.IsCancellationRequested)
            {
                Thread.Sleep(25);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    /// <summary>The gate and, when it was free in time, the lock file.</summary>
    public sealed class Held : IDisposable
    {
        private readonly object _gate;
        private FileStream? _file;
        private bool _disposed;

        internal Held(object gate, FileStream? file)
        {
            _gate = gate;
            _file = file;
        }

        public bool HasFileLock => _file != null;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _file?.Dispose();
            _file = null;
            Monitor.Exit(_gate);
        }
    }
}
