using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using HermesProxy.World.Server.AccountSync;
using Xunit;

namespace HermesProxy.Tests.World.AccountSync;

/// <summary>The CUF structure check against the files the 1.14.2 client really wrote (P0 samples, test account
/// SYNCTEST1) and the measured layout: u32 profile count, per profile 19 fixed bytes (name length &lt;&lt; 1 first)
/// and the name. Same names in proxy, launcher and server. Placebo: going back to "first byte ≤ 5" turns
/// <see cref="RawCufValidatorRejectsBrokenWalk"/> red.</summary>
public class RawCufValidatorTests
{
    internal static readonly byte[] SamplePrimary = Convert.FromHexString(
        "010000000e49e0002400480000000000000000000000005072696d617279");
    internal static readonly byte[] SampleProbeA = Convert.FromHexString(
        "020000000e49e0002400480000000000000000000000005072696d6172790c49e00024004800000000000000000000000050726f626541");
    internal static readonly byte[] SampleProbeB = Convert.FromHexString(
        "020000000e49e0002400480000000000000000000000005072696d6172790c49e00024004800000000000000000000000050726f626542");

    /// <summary>A block in the measured layout; the 18 bytes after the length byte are the ones the client wrote.</summary>
    internal static byte[] CufFile(params string[] names)
    {
        var bytes = new List<byte>(BitConverter.GetBytes((uint)names.Length));
        foreach (string name in names)
        {
            bytes.Add((byte)(name.Length << 1));
            bytes.AddRange(SamplePrimary.AsSpan(5, 18).ToArray());
            bytes.AddRange(Encoding.UTF8.GetBytes(name));
        }
        return bytes.ToArray();
    }

    [Fact]
    public void RawCufValidatorMatchesP0Samples()
    {
        Assert.True(RawCufValidator.Validate(SamplePrimary, RawCufValidator.PacketMaxBytes));
        Assert.True(RawCufValidator.Validate(SampleProbeA, RawCufValidator.PacketMaxBytes));
        Assert.True(RawCufValidator.Validate(SampleProbeB, RawCufValidator.PacketMaxBytes));
        Assert.True(RawCufValidator.Validate(new byte[4], RawCufValidator.PacketMaxBytes)); // the client default
        Assert.Equal(SamplePrimary, CufFile("Primary")); // the builder is the measured layout
    }

    [Fact]
    public void RawCufValidatorRejectsBrokenWalk()
    {
        byte[] a = SampleProbeA;
        byte[] sixCount = (byte[])a.Clone(); sixCount[0] = 6;
        byte[] nameOverEnd = (byte[])a.Clone(); nameOverEnd[30] = 0x7E; // the second profile claims a 63-byte name
        byte[] highByte = (byte[])a.Clone(); highByte[3] = 1;          // the count is a u32, not one byte
        foreach (byte[] broken in new[] { Array.Empty<byte>(), a[..^1], a.Concat(new byte[] { 0 }).ToArray(), sixCount, nameOverEnd, highByte })
            Assert.False(RawCufValidator.Validate(broken, RawCufValidator.PacketMaxBytes));
        Assert.False(RawCufValidator.Validate(new byte[RawCufValidator.PacketMaxBytes + 1], 128 * 1024));
    }

    [Fact]
    public void FamilyCeilingIsHonoured()
    {
        Assert.True(RawCufValidator.Validate(SampleProbeA, SampleProbeA.Length));
        Assert.False(RawCufValidator.Validate(SampleProbeA, SampleProbeA.Length - 1));
    }

    [Fact]
    public void LoadCufProfilesCarriesFiveLongProfiles()
    {
        byte[] five = CufFile(Enumerable.Range(0, 5).Select(i => $"Profile {i} ".PadRight(48, 'x')).ToArray());
        Assert.True(five.Length > 256, "positive control: larger than the old 256-byte cap");
        Assert.True(RawCufValidator.Validate(five, RawCufValidator.PacketMaxBytes));
        var packet = new HermesProxy.World.Server.Packets.LoadCUFProfiles { Data = five };
        Assert.True(packet.WriteToSpan(new byte[4096]) > 0, "the load packet carries the block");
    }
}
