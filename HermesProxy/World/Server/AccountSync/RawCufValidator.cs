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
/// <item>the layout is measured (P0): u32 profile count capped at <see cref="MaxProfiles"/>
/// (<c>PlayerConst.MaxCUFProfiles</c> in PlayerDefines.cs), then per profile 19 fixed bytes and the name;</item>
/// <item>absent means the default 4-byte block, so a 0-byte file is never a deliberate state.</item>
/// </list>
/// </summary>
public static class RawCufValidator
{
    /// <summary>The load packet's own ceiling (<c>ClientConfigPackets.cs</c> LoadCUFProfiles.MaxDataSize = 2048).</summary>
    public const int PacketMaxBytes = 2048;

    /// <summary>The client's profile cap (<c>PlayerConst.MaxCUFProfiles</c> in PlayerDefines.cs); the u32 at offset 0 counts profiles.</summary>
    public const int MaxProfiles = PlayerConst.MaxCUFProfiles;

    /// <summary>Fixed bytes of one profile before its name; the first carries the name length shifted left by one.</summary>
    public const int ProfileHeaderBytes = 19;

    /// <summary>True when <paramref name="bytes"/> is a whole CUF block within the family ceiling. Measured layout
    /// (P0, MESSUNG-P0.md#cuf): u32 LE profile count (at most <see cref="MaxProfiles"/>), then per profile
    /// <see cref="ProfileHeaderBytes"/> fixed bytes whose first byte &gt;&gt; 1 is the name length, then the name
    /// without a terminator; the file ends exactly after the last profile. "00 00 00 00" is the client default.</summary>
    public static bool Validate(ReadOnlySpan<byte> bytes, int maxBytes)
    {
        if (bytes.Length < 4 || bytes.Length > PacketMaxBytes || bytes.Length > maxBytes) return false;
        uint count = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        if (count > MaxProfiles) return false;
        int off = 4;
        for (uint i = 0; i < count; i++)
        {
            if (off + ProfileHeaderBytes > bytes.Length) return false;
            off += ProfileHeaderBytes + (bytes[off] >> 1);
            if (off > bytes.Length) return false;
        }
        return off == bytes.Length;
    }
}
