using System;
using System.Buffers.Binary;
using HermesProxy.World;
using HermesProxy.World.Client;
using HermesProxy.World.Enums;
using Xunit;

namespace HermesProxy.Tests.World;

// JimsProxy (2026-11-03): the cooldown block of a 1.12 SMSG_PET_SPELLS_MESSAGE. vMaNGOS since
// 5fe4f0a7b writes 12-byte entries [spell u16][category u16][cooldown u32][category cooldown u32];
// the parser read 8-byte entries for every pre-3.1 server (f672fb2), so from the second entry on
// it read spell ids out of the previous entry's category cooldown. Servers that do send the 8-byte
// shape must keep parsing. The test environment pins the server build to 1.12.1.
public class PetSpellsCooldownParseTests
{
    record Entry(ushort Spell, ushort Category, uint Cooldown, uint CategoryCooldown);

    static WorldPacket Block(bool withCategoryCooldown, params Entry[] entries)
    {
        int size = withCategoryCooldown ? 12 : 8;
        var bytes = new byte[1 + entries.Length * size];
        bytes[0] = (byte)entries.Length;
        for (int i = 0; i < entries.Length; i++)
        {
            var at = bytes.AsSpan(1 + i * size);
            BinaryPrimitives.WriteUInt16LittleEndian(at, entries[i].Spell);
            BinaryPrimitives.WriteUInt16LittleEndian(at[2..], entries[i].Category);
            BinaryPrimitives.WriteUInt32LittleEndian(at[4..], entries[i].Cooldown);
            if (withCategoryCooldown)
                BinaryPrimitives.WriteUInt32LittleEndian(at[8..], entries[i].CategoryCooldown);
        }
        return new WorldPacket((uint)Opcode.SMSG_PET_SPELLS_MESSAGE, bytes);
    }

    static readonly Entry Torment = new(3716, 0, 5000, 0);
    static readonly Entry Sacrifice = new(7812, 0, 0, 300000);

    [Fact]
    public void TwelveByteEntries_EveryEntryReadAtItsOwnOffset()
    {
        var packet = Block(true, Torment, Sacrifice);

        var cooldowns = WorldClient.ReadLegacyPetSpellCooldowns(packet);

        Assert.Equal(2, cooldowns.Count);
        Assert.Equal(3716u, cooldowns[0].SpellID);
        Assert.Equal(5000u, cooldowns[0].Duration);
        Assert.Equal(7812u, cooldowns[1].SpellID);
        Assert.Equal(300000u, cooldowns[1].CategoryDuration);
        Assert.False(packet.CanRead());
    }

    [Fact]
    public void EightByteEntries_StillParsed()
    {
        var packet = Block(false, Torment, Sacrifice);

        var cooldowns = WorldClient.ReadLegacyPetSpellCooldowns(packet);

        Assert.Equal(new uint[] { 3716, 7812 }, new[] { cooldowns[0].SpellID, cooldowns[1].SpellID });
        Assert.Equal(0u, cooldowns[1].CategoryDuration);
        Assert.False(packet.CanRead());
    }

    [Fact]
    public void NoEntries_EmptyList()
    {
        var packet = Block(true);

        Assert.Empty(WorldClient.ReadLegacyPetSpellCooldowns(packet));
    }
}
