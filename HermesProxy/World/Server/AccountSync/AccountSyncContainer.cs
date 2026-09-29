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

    /// <summary>Validates the header, identity, lengths, and zlib stream.</summary>
    public static bool Validate(ReadOnlySpan<byte> bytes, uint expectedType, WowGuid128 expectedGuid)
    {
        if (bytes.Length is < HeaderSize or > MaxFileSize) return false;
        ulong low = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        ulong high = BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]);
        uint type = BinaryPrimitives.ReadUInt32LittleEndian(bytes[24..]);
        uint unpackedSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes[28..]);
        int compressedSize = BinaryPrimitives.ReadInt32LittleEndian(bytes[32..]);
        if (type != expectedType || unpackedSize > MaxUncompressedSize || compressedSize < 0 || compressedSize != bytes.Length - HeaderSize)
            return false;
        if (expectedType == 3 && (low != expectedGuid.GetLowValue() || high != expectedGuid.GetHighValue()))
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
}
