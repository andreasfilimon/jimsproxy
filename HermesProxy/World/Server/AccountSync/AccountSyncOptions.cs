using System.Runtime.InteropServices;

namespace HermesProxy.World.Server.AccountSync;

/// <summary>Validated environment-only configuration for binding synchronization.</summary>
public sealed record AccountSyncOptions
{
    /// <summary>Whether synchronization is enabled.</summary>
    public bool Enabled { get; init; }
    /// <summary>Human-readable startup status that contains no secrets.</summary>
    public string StatusReason { get; init; } = "not configured";
    /// <summary>Normalized API base URL.</summary>
    public Uri? BaseUri { get; init; }
    /// <summary>Bearer credential used only in the Authorization header.</summary>
    public string Token { get; init; } = "";
    /// <summary>Canonical lower-case installation UUID.</summary>
    public string DeviceId { get; init; } = "";
    /// <summary>Privacy-safe device label.</summary>
    public string DeviceLabel { get; init; } = "";
    /// <summary>Optional launcher version header value.</summary>
    public string? LauncherVersion { get; init; }

    /// <summary>Reads and validates the ST_SYNC_* environment variables.</summary>
    public static AccountSyncOptions FromEnvironment(Func<string, string?>? read = null)
    {
        read ??= Environment.GetEnvironmentVariable;
        string? rawBase = read("ST_SYNC_BASE");
        string? token = read("ST_SYNC_TOKEN");
        string? rawDeviceId = read("ST_SYNC_DEVICE_ID");

        if (string.IsNullOrWhiteSpace(rawBase))
            return Off("ST_SYNC_BASE is missing");
        if (string.IsNullOrWhiteSpace(token))
            return Off("ST_SYNC_TOKEN is missing");
        if (string.IsNullOrWhiteSpace(rawDeviceId))
            return Off("ST_SYNC_DEVICE_ID is missing");
        if (!Uri.TryCreate(rawBase.Trim(), UriKind.Absolute, out var baseUri) || !IsAllowedBase(baseUri))
            return Off("ST_SYNC_BASE is invalid");

        string deviceId = rawDeviceId.Trim();
        if (!Guid.TryParseExact(deviceId, "D", out var parsedId) || deviceId != parsedId.ToString("D"))
            return Off("ST_SYNC_DEVICE_ID is not a lower-case UUID");

        string? configuredLabel = read("ST_SYNC_DEVICE_LABEL");
        string label = configuredLabel ?? $"JimsProxy {GetOsName()}";
        if (label.Length is < 1 or > 40 || label.Any(ch => ch is < ' ' or > '~'))
            return Off("ST_SYNC_DEVICE_LABEL is invalid");

        return new AccountSyncOptions
        {
            Enabled = true,
            StatusReason = "configured",
            BaseUri = new Uri(baseUri.AbsoluteUri.TrimEnd('/') + "/"),
            Token = token,
            DeviceId = deviceId,
            DeviceLabel = label,
            LauncherVersion = NullIfEmpty(read("ST_SYNC_LAUNCHER_VERSION")),
        };
    }

    private static bool IsAllowedBase(Uri uri)
    {
        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            return false;
        if (uri.Scheme == Uri.UriSchemeHttps)
            return true;
        return uri.Scheme == Uri.UriSchemeHttp &&
            (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || uri.Host == "127.0.0.1");
    }

    private static string GetOsName()
    {
        if (OperatingSystem.IsWindows()) return "Windows";
        if (OperatingSystem.IsMacOS()) return "macOS";
        if (OperatingSystem.IsLinux()) return "Linux";
        return RuntimeInformation.OSDescription.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "Unknown OS";
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static AccountSyncOptions Off(string reason) => new() { StatusReason = reason };
}
