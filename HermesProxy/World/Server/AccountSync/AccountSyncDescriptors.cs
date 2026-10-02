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

/// <summary>The shared descriptor table. Released 2026-10-02: bindings, macros, config, chat, cuf (maxBytes from
/// FREIGABE.json); layout, tts, flagged, clickbindings stay dormant.</summary>
public static class AccountSyncDescriptors
{
    public const string Flavor = "classic-era-42597";

    private const int PlaceholderMaxBytes = 128 * 1024;

    public static readonly IReadOnlyList<AccountSyncDescriptor> All =
    [
        new("bindings", 2, "account", AccountSyncFormat.Container, "data-2.bin", 128 * 1024, "bindings", true, true),
        new("bindings", 3, "character", AccountSyncFormat.Container, "data-3-<lo>-<hi>.bin", 128 * 1024, "bindings", true, true),
        new("macros", 4, "account", AccountSyncFormat.Container, "data-4.bin", 65536, "macros", true, true),
        new("macros", 5, "character", AccountSyncFormat.Container, "data-5-<lo>-<hi>.bin", 16384, "macros", true, true),
        new("config", 0, "account", AccountSyncFormat.Container, "data-0.bin", 32768, "config", true, true),
        new("config", 1, "character", AccountSyncFormat.Container, "data-1-<lo>-<hi>.bin", 32768, "config", true, true),
        new("layout", 6, "character", AccountSyncFormat.Container, "data-6-<lo>-<hi>.bin", PlaceholderMaxBytes, "layout", false, false),
        new("chat", 7, "character", AccountSyncFormat.Container, "data-7-<lo>-<hi>.bin", 16384, "chat", true, true),
        new("tts", 8, "account", AccountSyncFormat.Container, "data-8.bin", PlaceholderMaxBytes, "tts", false, false),
        new("tts", 9, "character", AccountSyncFormat.Container, "data-9-<lo>-<hi>.bin", PlaceholderMaxBytes, "tts", false, false),
        new("flagged", 10, "account", AccountSyncFormat.Container, "data-10.bin", PlaceholderMaxBytes, "flagged", false, false),
        new("flagged", 11, "character", AccountSyncFormat.Container, "data-11-<lo>-<hi>.bin", PlaceholderMaxBytes, "flagged", false, false),
        new("clickbindings", 12, "character", AccountSyncFormat.Container, "data-12-<lo>-<hi>.bin", PlaceholderMaxBytes, "clickbindings", false, false),
        new("cuf", null, "account", AccountSyncFormat.RawCuf, "cuf.bin", 2048, "cuf", true, true),
    ];

    private static readonly FrozenDictionary<uint, AccountSyncDescriptor> ByType =
        All.Where(d => d.Type is not null).ToFrozenDictionary(d => d.Type!.Value);

    /// <summary>The descriptor for an AccountData type, or null when no row carries it.</summary>
    public static AccountSyncDescriptor? ForType(uint type) => ForType(type, All);

    /// <summary>The type lookup over an explicit table (N1.2: tests inject a table with a family on).</summary>
    public static AccountSyncDescriptor? ForType(uint type, IReadOnlyList<AccountSyncDescriptor>? descriptors) =>
        (descriptors ?? All).FirstOrDefault(d => d.Type == type);

    /// <summary>The descriptor whose file-name pattern a proxy output name matches (RecoverLeftovers).</summary>
    public static AccountSyncDescriptor? ForFileName(string fileName) => ForFileName(fileName, All);

    /// <summary>Account files match exactly; character files match with canonical u64 GUID halves
    /// (<c>data-5-2361-0.bin</c> matches <c>data-5-&lt;lo&gt;-&lt;hi&gt;.bin</c>, <c>data-5-x-0.bin</c> and a
    /// leading zero do not).</summary>
    public static AccountSyncDescriptor? ForFileName(string fileName, IReadOnlyList<AccountSyncDescriptor>? descriptors)
    {
        foreach (var descriptor in descriptors ?? All)
            if (FileNameMatches(descriptor.FileName, fileName)) return descriptor;
        return null;
    }

    internal static bool FileNameMatches(string pattern, string fileName)
    {
        int lo = pattern.IndexOf("<lo>", StringComparison.Ordinal);
        if (lo < 0) return string.Equals(pattern, fileName, StringComparison.Ordinal);
        int hi = pattern.IndexOf("<hi>", lo + 4, StringComparison.Ordinal);
        if (hi < 0) return false;
        string prefix = pattern[..lo];
        string mid = pattern[(lo + 4)..hi];
        string suffix = pattern[(hi + 4)..];
        if (!fileName.StartsWith(prefix, StringComparison.Ordinal) || !fileName.EndsWith(suffix, StringComparison.Ordinal))
            return false;
        string inner = fileName[prefix.Length..(fileName.Length - suffix.Length)];
        int midIndex = inner.IndexOf(mid, StringComparison.Ordinal);
        if (midIndex <= 0) return false;
        return IsCanonicalU64(inner[..midIndex]) && IsCanonicalU64(inner[(midIndex + mid.Length)..]);
    }

    private static bool IsCanonicalU64(string s) =>
        s.Length > 0 && (s.Length == 1 || s[0] != '0') && ulong.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out _);

    /// <summary>The rows the proxy drives at world entry and after a save (all of them; the caller filters
    /// the injected table's ImplementedByProxy rows).</summary>
    public static IEnumerable<AccountSyncDescriptor> ImplementedByProxy => All.Where(d => d.ImplementedByProxy);
}
