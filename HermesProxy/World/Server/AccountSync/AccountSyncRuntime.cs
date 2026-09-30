using Framework.Logging;

namespace HermesProxy.World.Server.AccountSync;

/// <summary>Process-wide account-sync service used by the two narrow AccountData hooks.</summary>
public static class AccountSyncRuntime
{
    private static readonly HttpClient SharedHttpClient = new();
    private static AccountSyncService? _service;
    private static int _flushed;

    /// <summary>The configured process service, if startup initialized it.</summary>
    public static AccountSyncService? Service => Volatile.Read(ref _service);

    /// <summary>Initializes synchronization once from environment variables.</summary>
    public static void Initialize()
    {
        var options = AccountSyncOptions.FromEnvironment();
        _service = new AccountSyncService(options, SharedHttpClient);
        Log.Print(LogType.Server, $"account sync: {(options.Enabled ? "on" : "off")} ({options.StatusReason})");
    }

    /// <summary>Best-effort two-second shutdown flush.</summary>
    /// <remarks>Called from more than one place (normal stop and process exit); only the first call works.</remarks>
    public static void FlushForShutdown()
    {
        if (Interlocked.Exchange(ref _flushed, 1) != 0) return;
        // Deferred saves first: they are the player's own changes and do not depend on sync being on.
        if (AccountSyncDeferredSaves.ApplyPending(TimeSpan.FromSeconds(1)) > 0)
            Log.Print(LogType.Warn, "account sync: a save is still waiting for the AccountData lock; it is put in place at the next world entry");
        Service?.Shutdown(TimeSpan.FromSeconds(2));
    }

    /// <summary>Tests only: run the shutdown path against <paramref name="service"/> as if it were the process service.</summary>
    internal static void UseForTest(AccountSyncService? service)
    {
        Volatile.Write(ref _service, service);
        Volatile.Write(ref _flushed, 0);
    }
}
