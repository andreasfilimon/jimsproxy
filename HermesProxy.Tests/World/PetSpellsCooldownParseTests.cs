using System;
using System.Buffers.Binary;
using HermesProxy.World;
using HermesProxy.World.Client;
using HermesProxy.World.Enums;
using Xunit;
using Layout = HermesProxy.World.Client.WorldClient.PreWotlkPetCooldownLayout;

namespace HermesProxy.Tests.World;

// JimsProxy (2026-11-03): the cooldown block that ends a 1.12 SMSG_PET_SPELLS_MESSAGE. vMaNGOS since
// 5fe4f0a7b writes [count u8] + 12-byte entries [spell u16][category u16][cooldown u32][category cooldown u32];
// vMaNGOS before it wrote [count u16] + 14-byte entries with a u32 spell id; Twinstar/Kronos send 8-byte
// entries without the category cooldown (f672fb2, 144-byte packet, 7 entries ending exactly at the packet
// end). The parser must read each shape at its own offsets. The test environment pins the server build to 1.12.1.
public class PetSpellsCooldownParseTests
{
    record Entry(ushort Spell, ushort Category, uint Cooldown, uint CategoryCooldown);

    static readonly Entry Torment = new(3716, 0, 5000, 0);
    static readonly Entry Sacrifice = new(7812, 0, 0, 300000);

    static byte[] Block(int entrySize, int trailing, params Entry[] entries)
    {
        bool wideCount = entrySize == 14;
        int head = wideCount ? 2 : 1;
        var bytes = new byte[head + entries.Length * entrySize + trailing];
        if (wideCount)
            BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)entries.Length);
        else
            bytes[0] = (byte)entries.Length;
        for (int i = 0; i < entries.Length; i++)
        {
            var at = bytes.AsSpan(head + i * entrySize);
            int spellWidth = wideCount ? 4 : 2;
            if (wideCount)
                BinaryPrimitives.WriteUInt32LittleEndian(at, entries[i].Spell);
            else
                BinaryPrimitives.WriteUInt16LittleEndian(at, entries[i].Spell);
            BinaryPrimitives.WriteUInt16LittleEndian(at[spellWidth..], entries[i].Category);
            BinaryPrimitives.WriteUInt32LittleEndian(at[(spellWidth + 2)..], entries[i].Cooldown);
            if (entrySize != 8)
                BinaryPrimitives.WriteUInt32LittleEndian(at[(spellWidth + 6)..], entries[i].CategoryCooldown);
        }
        for (int i = bytes.Length - trailing; i < bytes.Length; i++)
            bytes[i] = 0xEE;
        return bytes;
    }

    static WorldPacket Packet(byte[] block) => new((uint)Opcode.SMSG_PET_SPELLS_MESSAGE, block);

    [Fact]
    public void TwelveByteEntries_EveryEntryReadAtItsOwnOffset()
    {
        var packet = Packet(Block(12, 0, Torment, Sacrifice));

        var cooldowns = WorldClient.ReadLegacyPetSpellCooldowns(packet);

        Assert.Equal(2, cooldowns.Count);
        Assert.Equal(3716u, cooldowns[0].SpellID);
        Assert.Equal(5000u, cooldowns[0].Duration);
        Assert.Equal(7812u, cooldowns[1].SpellID);
        Assert.Equal(300000u, cooldowns[1].CategoryDuration);
        Assert.False(packet.CanRead());
    }

    [Fact]
    public void EightByteEntries_TwinstarShape_StillParsed()
    {
        var entries = new[] { Torment, Sacrifice, Torment, Sacrifice, Torment, Sacrifice, Torment };

        var cooldowns = WorldClient.ParsePreWotlkPetCooldowns(Block(8, 0, entries), out var layout);

        Assert.Equal(Layout.EightByte, layout);
        Assert.Equal(7, cooldowns.Count);
        Assert.Equal(7812u, cooldowns[5].SpellID);
        Assert.Equal(0u, cooldowns[5].CategoryDuration);
        Assert.Equal(3716u, cooldowns[6].SpellID);
    }

    [Fact]
    public void VmangosBeforeTheLayoutChange_WideCountAndSpellId()
    {
        // What a core rolled back to b38750581 sends: [count u16] + 14-byte entries.
        var packet = Packet(Block(14, 0, Torment, Sacrifice));

        var cooldowns = WorldClient.ReadLegacyPetSpellCooldowns(packet);

        Assert.Equal(new uint[] { 3716, 7812 }, new[] { cooldowns[0].SpellID, cooldowns[1].SpellID });
        Assert.Equal(5000u, cooldowns[0].Duration);
        Assert.Equal(300000u, cooldowns[1].CategoryDuration);
        Assert.False(packet.CanRead());
    }

    [Fact]
    public void EightByteEntriesWithTrailingBytes_KeepTheEightByteReading()
    {
        // 9 trailing bytes: enough for a "remaining >= count * 12" rule to take them as category
        // cooldowns, but no exact shape, so the pre-2026-11-03 reading stays.
        var cooldowns = WorldClient.ParsePreWotlkPetCooldowns(Block(8, 9, Torment, Sacrifice), out var layout);

        Assert.Equal(Layout.Unrecognized, layout);
        Assert.Equal(new uint[] { 3716, 7812 }, new[] { cooldowns[0].SpellID, cooldowns[1].SpellID });
        Assert.Equal(0u, cooldowns[1].CategoryDuration);
    }

    [Fact]
    public void NoEntries_EmptyList()
    {
        var packet = Packet(Block(12, 0));

        Assert.Empty(WorldClient.ReadLegacyPetSpellCooldowns(packet));
        Assert.False(packet.CanRead());
    }

    [Fact]
    public void BlockShorterThanItsCount_Throws()
    {
        // The handler's wrapper turns this into a dropped pet bar, as before the change.
        var block = Block(8, 0, Torment, Sacrifice)[..9];

        Assert.Throws<ArgumentOutOfRangeException>(() => WorldClient.ParsePreWotlkPetCooldowns(block, out _));
    }
}
