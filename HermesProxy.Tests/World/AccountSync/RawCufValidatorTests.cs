using System;
using System.Collections.Generic;
using System.Linq;
using HermesProxy.World.Server.AccountSync;
using Xunit;

namespace HermesProxy.Tests.World.AccountSync;

/// <summary>The CUF structure fixture: three whole blocks and three broken ones, built from the criterion
/// in CUF-STRUKTUR.md (first byte = profile count ≤ 5, 1..256 bytes). Placebo: relaxing any bound turns a
/// "broken" row green.</summary>
public class RawCufValidatorTests
{
    // (profiles, length, valid)
    public static IEnumerable<object[]> Fixture() =>
    [
        [0, 4, true],      // the default block (LoadCUFProfiles absent -> new byte[4])
        [2, 30, true],     // a typical small block
        [5, 256, true],    // the maximum the load packet accepts
        [0, 0, false],     // empty is never a deliberate state
        [1, 257, false],   // over the load packet's 256-byte cap
        [6, 4, false],     // profile count above PlayerConst.MaxCUFProfiles
    ];

    [Theory]
    [MemberData(nameof(Fixture))]
    public void StructureFixture(int profiles, int length, bool valid)
    {
        byte[] bytes = CufBlock(profiles, length);
        Assert.Equal(valid, RawCufValidator.Validate(bytes, 128 * 1024));
    }

    [Fact]
    public void FamilyCeilingIsHonoured()
    {
        Assert.True(RawCufValidator.Validate(CufBlock(1, 100), 100));
        Assert.False(RawCufValidator.Validate(CufBlock(1, 100), 99));
        // The family's own ceiling never overrides the packet cap.
        Assert.False(RawCufValidator.Validate(CufBlock(1, 257), 128 * 1024));
    }

    internal static byte[] CufBlock(int profiles, int length)
    {
        var bytes = new byte[length];
        if (length > 0) bytes[0] = (byte)profiles;
        for (int i = 1; i < length; i++) bytes[i] = (byte)(i & 0xFF);
        return bytes;
    }
}
