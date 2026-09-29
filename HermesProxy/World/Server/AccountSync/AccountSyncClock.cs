namespace HermesProxy.World.Server.AccountSync;

/// <summary>Injectable time and delay source for deterministic synchronization tests.</summary>
public interface IAccountSyncClock
{
    /// <summary>Current UTC time.</summary>
    DateTimeOffset UtcNow { get; }
    /// <summary>Asynchronously waits for a duration.</summary>
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

internal sealed class SystemAccountSyncClock : IAccountSyncClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken);
}
