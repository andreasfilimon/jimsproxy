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
        Assert.True(AccountSyncContainer.Validate(RealData, 2, default));

    [Fact]
    public void Validate_WrongType_Rejects() =>
        Assert.False(AccountSyncContainer.Validate(RealData, 3, new WowGuid128(2361, 0)));

    [Fact]
    public void Validate_Type3WrongGuid_Rejects()
    {
        byte[] bytes = (byte[])RealData.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), 3);
        Assert.False(AccountSyncContainer.Validate(bytes, 3, new WowGuid128(1, 2)));
    }

    [Fact]
    public void Validate_BadCompressedLength_Rejects()
    {
        byte[] bytes = (byte[])RealData.Clone();
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(32), 1);
        Assert.False(AccountSyncContainer.Validate(bytes, 2, default));
    }
}
