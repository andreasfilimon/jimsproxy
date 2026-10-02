using System;
using System.Buffers.Binary;
using HermesProxy.World;
using HermesProxy.World.Server.AccountSync;
using Xunit;

namespace HermesProxy.Tests.World.AccountSync;

public class AccountSyncContainerTests
{
    internal static readonly byte[] RealData = Convert.FromBase64String(
        "OQgAAAAAAAAAAAAAAAQACHy/umoAAAAAAgAAACcAAAAqAAAAeAFLysxLUXCzVPAK9Q3g5UoC8wwNFEL83d19XMP9g3xcfB2BEgDbdApz");

    [Fact]
    public void Validate_RealType2File_Accepts() =>
        Assert.True(AccountSyncContainer.Validate(RealData, 2, default, requireGuidMatch: false));

    [Fact]
    public void Validate_WrongType_Rejects() =>
        Assert.False(AccountSyncContainer.Validate(RealData, 3, new WowGuid128(2361, 0), requireGuidMatch: true));

    [Fact]
    public void Validate_Type3WrongGuid_Rejects()
    {
        byte[] bytes = (byte[])RealData.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), 3);
        Assert.False(AccountSyncContainer.Validate(bytes, 3, new WowGuid128(1, 2), requireGuidMatch: true));
    }

    [Fact]
    public void ContentSha256_IgnoresOnlyTheTimeField()
    {
        byte[] restamped = (byte[])RealData.Clone();
        BinaryPrimitives.WriteInt64LittleEndian(restamped.AsSpan(16), 1790972886);
        Assert.NotEqual(AccountSyncContainer.Sha256(RealData), AccountSyncContainer.Sha256(restamped));
        Assert.Equal(AccountSyncContainer.ContentSha256(RealData), AccountSyncContainer.ContentSha256(restamped));
    }

    [Theory]
    [InlineData(0)]   // GUID
    [InlineData(15)]  // GUID, last byte before the time field
    [InlineData(24)]  // type, first byte after the time field
    [InlineData(28)]  // uncompressed size
    [InlineData(36)]  // first byte of the zlib stream
    [InlineData(-1)]  // last byte
    public void ContentSha256_SeesEveryOtherByte(int offset)
    {
        byte[] changed = (byte[])RealData.Clone();
        int at = offset < 0 ? changed.Length + offset : offset;
        changed[at] ^= 0x01;
        Assert.NotEqual(AccountSyncContainer.ContentSha256(RealData), AccountSyncContainer.ContentSha256(changed));
    }

    [Fact]
    public void ContentSha256_ShorterThanTheHeader_IsThePlainHash() =>
        Assert.Equal(AccountSyncContainer.Sha256(new byte[] { 1, 2, 3 }), AccountSyncContainer.ContentSha256(new byte[] { 1, 2, 3 }));

    [Fact]
    public void Validate_BadCompressedLength_Rejects()
    {
        byte[] bytes = (byte[])RealData.Clone();
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(32), 1);
        Assert.False(AccountSyncContainer.Validate(bytes, 2, default, requireGuidMatch: false));
    }
}
