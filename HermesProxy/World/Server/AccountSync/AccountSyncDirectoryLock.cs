using System.Collections.Concurrent;

namespace HermesProxy.World.Server.AccountSync;

/// <summary>Provides the process-wide synchronous gate for one AccountData directory.</summary>
public static class AccountSyncDirectoryLock
{
    private static readonly ConcurrentDictionary<string, object> Gates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    /// <summary>Returns the shared gate for all local writes in the directory.</summary>
    public static object For(string directory) => Gates.GetOrAdd(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)), static _ => new object());
}
