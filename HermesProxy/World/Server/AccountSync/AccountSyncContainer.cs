using System.Buffers.Binary;
using System.IO.Compression;

namespace HermesProxy.World.Server.AccountSync;

/// <summary>Validation and hashing for whole AccountData binding files.</summary>
public static class AccountSyncContainer
{
    /// <summary>Maximum accepted whole-file size.</summary>
    public const int MaxFileSize = 128 * 1024;
    /// <summary>Maximum accepted inflated payload size.</summary>
    public const int MaxUncompressedSize = 1024 * 1024;
    private const int HeaderSize = 36;

    /// <summary>Validates the header, identity, lengths, and zlib stream. The file-size ceiling comes
    /// from the descriptor (<paramref name="maxFileSize"/>) so a family's own limit is honoured. The GUID
    /// is checked for every character-scope descriptor (<paramref name="requireGuidMatch"/>, required);
    /// the caller passes <c>descriptor.IsCharacter</c>, there is no type-number fallback (K2 B2).</summary>
    public static bool Validate(ReadOnlySpan<byte> bytes, uint expectedType, WowGuid128 expectedGuid,
        bool requireGuidMatch, int maxFileSize = MaxFileSize)
    {
        if (bytes.Length < HeaderSize || bytes.Length > maxFileSize) return false;
        ulong low = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        ulong high = BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]);
        uint type = BinaryPrimitives.ReadUInt32LittleEndian(bytes[24..]);
        uint unpackedSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes[28..]);
        int compressedSize = BinaryPrimitives.ReadInt32LittleEndian(bytes[32..]);
        if (type != expectedType || unpackedSize > MaxUncompressedSize || compressedSize < 0 || compressedSize != bytes.Length - HeaderSize)
            return false;
        if (requireGuidMatch && (low != expectedGuid.GetLowValue() || high != expectedGuid.GetHighValue()))
            return false;

        try
        {
            using var source = new MemoryStream(bytes[HeaderSize..].ToArray(), writable: false);
            using var zlib = new ZLibStream(source, CompressionMode.Decompress);
            byte[] buffer = new byte[8192];
            long total = 0;
            int read;
            while ((read = zlib.Read(buffer)) != 0)
            {
                total += read;
                if (total > MaxUncompressedSize || total > unpackedSize) return false;
            }
            return total == unpackedSize && source.Position == source.Length;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Returns a lower-case SHA-256 digest.</summary>
    public static string Sha256(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));

    /// <summary>SHA-256 of the file with the header's time field (bytes 16-23) zeroed. The client rewrites some
    /// documents with a new time and unchanged content (chat, type 7: measured 2026-10-02 in the staging world, two
    /// saves one second apart with identical text, each uploaded as its own revision). The decision table uses this hash to see
    /// that as "unchanged". Every other byte still counts, so any real change gives a different hash.</summary>
    public static string ContentSha256(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderSize) return Sha256(bytes);
        byte[] copy = bytes.ToArray();
        copy.AsSpan(16, 8).Clear();
        return Sha256(copy);
    }
}
