using HermesProxy.World.Enums;

namespace HermesProxy.World.Server.AccountSync;

/// <summary>
/// Structure check for the RawCuf document (<c>cuf.bin</c>), shared byte-for-byte with the server
/// (lib/launcher-sync.ts) and the launcher (ModernAccountDataFiles). Derived from the proxy source
/// (docs/ACCOUNT-SYNC.md, deploy/account-sync-alle-typen/CUF-STRUKTUR.md):
/// <list type="bullet">
/// <item>the file is the raw <c>CMSG_SAVE_CUF_PROFILES</c> payload, written verbatim
/// (<c>AccountDataManager.SaveCUFProfiles</c>), never a 36-byte container;</item>
/// <item>the load packet refuses more than <see cref="PacketMaxBytes"/> bytes
/// (<c>LoadCUFProfiles.MaxDataSize</c>);</item>
/// <item>the first byte is the profile count and the client caps it at
/// <see cref="MaxProfiles"/> (<c>PlayerConst.MaxCUFProfiles</c> in PlayerDefines.cs);</item>
/// <item>absent means the default 4-byte block, so a 0-byte file is never a deliberate state.</item>
/// </list>
/// </summary>
public static class RawCufValidator
{
    /// <summary>The load packet's own ceiling (<c>ClientConfigPackets.cs</c> LoadCUFProfiles.MaxDataSize = 256).</summary>
    public const int PacketMaxBytes = 256;

    /// <summary>The client's profile cap (<c>PlayerConst.MaxCUFProfiles</c> in PlayerDefines.cs); the first byte counts profiles.</summary>
    public const int MaxProfiles = PlayerConst.MaxCUFProfiles;

    /// <summary>True when <paramref name="bytes"/> is a plausibly whole CUF block within the family ceiling.</summary>
    public static bool Validate(ReadOnlySpan<byte> bytes, int maxBytes) =>
        bytes.Length >= 1
        && bytes.Length <= PacketMaxBytes
        && bytes.Length <= maxBytes
        && bytes[0] <= MaxProfiles;
}
