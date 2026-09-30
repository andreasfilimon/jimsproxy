using System.Collections.Concurrent;
using System.Diagnostics;

namespace HermesProxy.World.Server.AccountSync;

/// <summary>
/// Serializes every write of the final AccountData files in one directory: against the Stonetavern
/// launcher through the lock file <see cref="FileName"/>, and within this process through a shared gate.
/// Both sides open the lock file with <see cref="FileShare.None"/> (a share mode on Windows, a
/// <c>flock</c> on Linux and macOS) and hold it only for the final check and move. The operating system
/// drops the lock when a process dies; the file itself is never deleted, so both sides always lock the
/// same file. A final file is never written without the lock: a save that cannot get it is deferred
/// (<see cref="AccountSyncDeferredSaves"/>). Contract: docs/ACCOUNT-SYNC.md, "Lock shared with the launcher".
/// </summary>
public static class AccountSyncDirectoryLock
{
    /// <summary>Name of the lock file; the launcher uses the same name. Change both together.</summary>
    public const string FileName = ".st-sync.lock";

    /// <summary>How long a client save waits before it is deferred. The launcher holds the lock for milliseconds.</summary>
    public static readonly TimeSpan SaveWait = TimeSpan.FromSeconds(1);

    /// <summary>How long a restore waits before it gives up and leaves the file for the next world entry.</summary>
    public static readonly TimeSpan RestoreWait = TimeSpan.FromSeconds(2);

    private static readonly ConcurrentDictionary<string, object> Gates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    /// <summary>True when the .NET runtime was told not to lock files (<c>DOTNET_SYSTEM_IO_DISABLEFILELOCKING</c>
    /// or the <c>System.IO.DisableFileLocking</c> switch). The lock file then excludes nobody, so account sync
    /// must stay off. Measured 2026-09-30: with the variable set, a .NET open succeeds while flock(1) holds the file.</summary>
    public static bool FileLockingDisabled =>
        (AppContext.TryGetSwitch("System.IO.DisableFileLocking", out bool disabled) && disabled) ||
        IsTrue(Environment.GetEnvironmentVariable("DOTNET_SYSTEM_IO_DISABLEFILELOCKING"));

    private static bool IsTrue(string? value) =>
        value != null && (value.Trim() == "1" || value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase));

    /// <summary>Returns the shared in-process gate for all local writes in the directory.</summary>
    public static object For(string directory) => Gates.GetOrAdd(Normalize(directory), static _ => new object());

    /// <summary>Tries for the lock file for up to <paramref name="wait"/> or until <paramref name="token"/> is
    /// cancelled, whichever comes first, WITHOUT holding the in-process gate (so no other writer of this
    /// process waits behind the poll), then takes the gate. The gate is always held on return;
    /// <see cref="Held.HasFileLock"/> says whether the file lock is too. Dispose on the same thread.</summary>
    /// <remarks><paramref name="gateWait"/> bounds the wait for the gate as well (null = until free, for a
    /// client save that must end up either written or deferred). When the gate is not free in time the file
    /// lock is released again and the result holds nothing: <see cref="Held.HasFileLock"/> is false and the
    /// caller must not write.</remarks>
    public static Held Enter(string directory, TimeSpan wait, CancellationToken token = default, TimeSpan? gateWait = null)
    {
        string normalized = Normalize(directory);
        FileStream? file = TryOpen(Path.Combine(normalized, FileName), wait, token);
        object gate = For(normalized);
        try
        {
            if (gateWait is null)
            {
                Monitor.Enter(gate);
            }
            else
            {
                var clock = Stopwatch.StartNew();
                bool entered;
                while (!(entered = Monitor.TryEnter(gate, 25)) && clock.Elapsed < gateWait.Value && !token.IsCancellationRequested) { }
                if (!entered)
                {
                    file?.Dispose();
                    return new Held(null, null);
                }
            }
        }
        catch
        {
            file?.Dispose();
            throw;
        }
        return new Held(gate, file);
    }

    internal static string Normalize(string directory) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));

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
        private readonly object? _gate;
        private FileStream? _file;
        private bool _disposed;

        internal Held(object? gate, FileStream? file)
        {
            _gate = gate;
            _file = file;
        }

        public bool HasFileLock => _file != null;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_gate != null) Monitor.Exit(_gate);
            _file?.Dispose();
            _file = null;
        }
    }
}
