using Framework.Logging;

namespace HermesProxy.World.Server.AccountSync;

/// <summary>Process-wide account-sync service used by the two narrow AccountData hooks.</summary>
public static class AccountSyncRuntime
{
    private static readonly HttpClient SharedHttpClient = new();
    private static AccountSyncService? _service;

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
    public static void FlushForShutdown() => Service?.Shutdown(TimeSpan.FromSeconds(2));
}
