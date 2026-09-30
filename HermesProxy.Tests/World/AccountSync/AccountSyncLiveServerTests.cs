using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using HermesProxy.World;
using HermesProxy.World.Server.AccountSync;
using Xunit;

namespace HermesProxy.Tests.World.AccountSync;

/// <summary>
/// End-to-end against a REAL sync server, skipped unless ST_SYNC_E2E_BASE and ST_SYNC_E2E_TOKEN are set.
/// Two temp folders play two PCs of the same account. The server must have the switch "on" for the
/// token's account, and no documents for it yet. Never point this at production with a real account.
/// </summary>
public class AccountSyncLiveServerTests
{
    private static readonly string? Base = Environment.GetEnvironmentVariable("ST_SYNC_E2E_BASE");
    private static readonly string? Token = Environment.GetEnvironmentVariable("ST_SYNC_E2E_TOKEN");
    // A fresh character per run (type 3): the document is new on the server every time, so runs do not
    // meet the conflict an earlier run left behind (this test ends in one on purpose).
    private const ulong High = 576465150349934592;
    private static readonly ulong Low = (ulong)Random.Shared.NextInt64(10_000_000, 99_999_999);
    private static readonly WowGuid128 Guid = new(Low, High);
    private static readonly string FileName = $"data-3-{Low}-{High}.bin";

    private static AccountSyncService NewPc(string deviceId) => new(
        AccountSyncOptions.FromEnvironment(name => name switch
        {
            "ST_SYNC_BASE" => Base,
            "ST_SYNC_TOKEN" => Token,
            "ST_SYNC_DEVICE_ID" => deviceId,
            _ => null,
        }),
        new HttpClient());

    /// <summary>A valid type 3 file for <see cref="Guid"/> with these bindings, built like the proxy's SaveData.</summary>
    private static byte[] Bindings(string text)
    {
        byte[] raw = Encoding.ASCII.GetBytes(text);
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(raw);
        byte[] data = ms.ToArray();
        byte[] file = new byte[36 + data.Length];
        BinaryPrimitives.WriteUInt64LittleEndian(file, Low);
        BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(8), High);
        BinaryPrimitives.WriteInt64LittleEndian(file.AsSpan(16), 1790700000);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(24), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(28), (uint)raw.Length);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(32), data.Length);
        data.CopyTo(file, 36);
        return file;
    }

    private static string Dir()
    {
        string d = Path.Combine(Path.GetTempPath(), "st-sync-e2e-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void TwoPcs_UploadRestoreAndFollowUpChange()
    {
        Assert.SkipWhen(string.IsNullOrEmpty(Base) || string.IsNullOrEmpty(Token), "ST_SYNC_E2E_BASE/TOKEN not set");
        string pcA = Dir(), pcB = Dir();
        using var a = NewPc("11111111-1111-4111-8111-111111111111");
        using var b = NewPc("22222222-2222-4222-8222-222222222222");
        Assert.True(a.IsEnabled);

        // PC A: first upload decided at world entry.
        byte[] first = Bindings("bind F9 JUMP\r\n");
        File.WriteAllBytes(Path.Combine(pcA, FileName), first);
        a.ReconcileAtWorldEntry(pcA, "Elwynn", Guid);
        a.FlushPending(TimeSpan.FromSeconds(10));
        Assert.True(File.Exists(Path.Combine(pcA, "sync-journal.json")), "A has no journal: upload did not land");

        // PC B: empty folder, fresh install. World entry must restore A's exact bytes.
        b.ReconcileAtWorldEntry(pcB, "Elwynn", Guid);
        Assert.Equal(first, File.ReadAllBytes(Path.Combine(pcB, FileName)));

        // PC A changes bindings in game (SaveData), uploads after the save.
        byte[] changed = Bindings("bind F8 JUMP\r\n");
        File.WriteAllBytes(Path.Combine(pcA, FileName), changed);
        a.EnqueueSaved(pcA, "Elwynn", Guid, 3);
        a.FlushPending(TimeSpan.FromSeconds(10));

        // PC B next world entry: local unchanged since confirmed, server newer -> restore, with backup.
        b.ReconcileAtWorldEntry(pcB, "Elwynn", Guid);
        Assert.Equal(changed, File.ReadAllBytes(Path.Combine(pcB, FileName)));
        Assert.Single(Directory.GetFiles(Path.Combine(pcB, "sync-backups")));

        // Both changed offline: B uploads, then A's divergent save must become a conflict candidate,
        // and A keeps its own file.
        byte[] fromB = Bindings("bind F7 JUMP\r\n");
        File.WriteAllBytes(Path.Combine(pcB, FileName), fromB);
        b.EnqueueSaved(pcB, "Elwynn", Guid, 3);
        b.FlushPending(TimeSpan.FromSeconds(10));
        byte[] fromA = Bindings("bind F6 JUMP\r\n");
        File.WriteAllBytes(Path.Combine(pcA, FileName), fromA);
        a.EnqueueSaved(pcA, "Elwynn", Guid, 3);
        a.FlushPending(TimeSpan.FromSeconds(10));
        Assert.Equal(fromA, File.ReadAllBytes(Path.Combine(pcA, FileName)));

        // Now the document is in conflict: B must not be overwritten by anything at world entry.
        b.ReconcileAtWorldEntry(pcB, "Elwynn", Guid);
        Assert.Equal(fromB, File.ReadAllBytes(Path.Combine(pcB, FileName)));
    }
}
