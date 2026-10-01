using System.Collections.Frozen;
using System.Globalization;

namespace HermesProxy.World.Server.AccountSync;

/// <summary>How the bytes of a document are shaped. <see cref="Container"/> is the 36-byte AccountData
/// header plus a zlib stream; <see cref="RawCuf"/> is the CUF profile file as the client writes it.</summary>
public enum AccountSyncFormat
{
    Container,
    RawCuf,
}

/// <summary>
/// One row of the shared account-sync descriptor table (plan
/// projects/wow/launcher/PLAN-account-sync-alle-typen-2026-10-01.md §2). The same content lives in the
/// server's lib/sync-descriptors.ts and the launcher's SyncDescriptors.cs, guarded by DescriptorsMatchFixture.
/// No read, upload, restore or validation may switch on a type number again — it asks this table.
/// </summary>
public sealed record AccountSyncDescriptor(
    string Family,
    uint? Type,
    string Scope,
    AccountSyncFormat Format,
    string FileName,
    int MaxBytes,
    string SwitchFamily,
    bool ImplementedByProxy,
    bool ImplementedByLauncher)
{
    /// <summary>Whether the key carries a character GUID (scope "character").</summary>
    public bool IsCharacter => Scope == "character";

    /// <summary>The key/file-name segment: the decimal type, or "-" for a RawCuf document.</summary>
    public string TypeSegment => Type is uint t ? t.ToString(CultureInfo.InvariantCulture) : "-";

    /// <summary>The proxy's file name with the GUID halves substituted for this document.</summary>
    public string FileNameFor(ulong low, ulong high) => FileName
        .Replace("<lo>", low.ToString(CultureInfo.InvariantCulture))
        .Replace("<hi>", high.ToString(CultureInfo.InvariantCulture));
}

/// <summary>The shared descriptor table. In P1 only bindings 2/3 are implemented by proxy and launcher;
/// every other row is dormant until its family is released (FREIGABE.json / P4).</summary>
public static class AccountSyncDescriptors
{
    public const string Flavor = "classic-era-42597";

    private const int PlaceholderMaxBytes = 128 * 1024;

    public static readonly IReadOnlyList<AccountSyncDescriptor> All =
    [
        new("bindings", 2, "account", AccountSyncFormat.Container, "data-2.bin", 128 * 1024, "bindings", true, true),
        new("bindings", 3, "character", AccountSyncFormat.Container, "data-3-<lo>-<hi>.bin", 128 * 1024, "bindings", true, true),
        new("macros", 4, "account", AccountSyncFormat.Container, "data-4.bin", PlaceholderMaxBytes, "macros", false, false),
        new("macros", 5, "character", AccountSyncFormat.Container, "data-5-<lo>-<hi>.bin", PlaceholderMaxBytes, "macros", false, false),
        new("config", 0, "account", AccountSyncFormat.Container, "data-0.bin", PlaceholderMaxBytes, "config", false, false),
        new("config", 1, "character", AccountSyncFormat.Container, "data-1-<lo>-<hi>.bin", PlaceholderMaxBytes, "config", false, false),
        new("layout", 6, "character", AccountSyncFormat.Container, "data-6-<lo>-<hi>.bin", PlaceholderMaxBytes, "layout", false, false),
        new("chat", 7, "character", AccountSyncFormat.Container, "data-7-<lo>-<hi>.bin", PlaceholderMaxBytes, "chat", false, false),
        new("tts", 8, "account", AccountSyncFormat.Container, "data-8.bin", PlaceholderMaxBytes, "tts", false, false),
        new("tts", 9, "character", AccountSyncFormat.Container, "data-9-<lo>-<hi>.bin", PlaceholderMaxBytes, "tts", false, false),
        new("flagged", 10, "account", AccountSyncFormat.Container, "data-10.bin", PlaceholderMaxBytes, "flagged", false, false),
        new("flagged", 11, "character", AccountSyncFormat.Container, "data-11-<lo>-<hi>.bin", PlaceholderMaxBytes, "flagged", false, false),
        new("clickbindings", 12, "character", AccountSyncFormat.Container, "data-12-<lo>-<hi>.bin", PlaceholderMaxBytes, "clickbindings", false, false),
        new("cuf", null, "account", AccountSyncFormat.RawCuf, "cuf.bin", PlaceholderMaxBytes, "cuf", false, false),
    ];

    private static readonly FrozenDictionary<uint, AccountSyncDescriptor> ByType =
        All.Where(d => d.Type is not null).ToFrozenDictionary(d => d.Type!.Value);

    private static readonly FrozenDictionary<string, AccountSyncDescriptor> ByFileName =
        All.ToFrozenDictionary(d => d.FileName, StringComparer.Ordinal);

    /// <summary>The descriptor for an AccountData type, or null when no row carries it.</summary>
    public static AccountSyncDescriptor? ForType(uint type) => ByType.GetValueOrDefault(type);

    /// <summary>The descriptor for a proxy output file name (e.g. for RecoverLeftovers), or null.</summary>
    public static AccountSyncDescriptor? ForFileName(string fileName) => ByFileName.GetValueOrDefault(fileName);

    /// <summary>The rows the proxy drives at world entry and after a save.</summary>
    public static IEnumerable<AccountSyncDescriptor> ImplementedByProxy => All.Where(d => d.ImplementedByProxy);
}
