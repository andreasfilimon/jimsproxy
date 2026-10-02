using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HermesProxy.World;
using HermesProxy.World.Server.AccountSync;
using Xunit;

namespace HermesProxy.Tests.World.AccountSync;

/// <summary>P4, the remaining families (N1). Released 2026-10-02: config, chat and cuf are live in the shipped
/// table; layout, tts, flagged and clickbindings stay off, so the proxy never touches them; the real code path
/// runs with an injected test table where one family is on (N1.2). Every file is synthetic. Placebo: the family's descriptor row gets the wrong
/// scope in the shipped table, so the family-on key/validation no longer matches and the test goes red.</summary>
public class AccountSyncFamiliesDarkTests
{
    private static readonly WowGuid128 Guid = new(2361, 0);

    private static IReadOnlyList<AccountSyncDescriptor> WithFamilies(params string[] families) =>
        AccountSyncDescriptors.All
            .Select(d => families.Contains(d.Family) ? d with { ImplementedByProxy = true } : d)
            .ToArray();

    // ── config (0 account / 1 character) ─────────────────────────────────────────────────────────────

    [Fact]
    public void ConfigIsLiveInShippedTable()
    {
        Assert.True(AccountSyncDescriptors.ForType(0)!.ImplementedByProxy);
        Assert.True(AccountSyncDescriptors.ForType(1)!.ImplementedByProxy);
        Assert.Equal("v1/classic-era-42597/config/account/elwynn/-/0", AccountSyncKeys.Build("Elwynn", Guid, 0));
        Assert.Equal("v1/classic-era-42597/config/character/elwynn/2361-0/1", AccountSyncKeys.Build("Elwynn", Guid, 1));
    }

    [Fact]
    public void ConfigOnlyCapabilityUploadsOnlyConfig()
    {
        OnlyFamilyUploads("config", 0, character: false);
    }

    // ── layout (6 character) ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void LayoutStaysDarkInShippedTable()
    {
        Assert.False(AccountSyncDescriptors.ForType(6)!.ImplementedByProxy);
        Assert.Null(AccountSyncKeys.Build("Elwynn", Guid, 6));
    }

    [Fact]
    public void LayoutOnlyCapabilityUploadsOnlyLayout()
    {
        OnlyFamilyUploads("layout", 6, character: true);
    }

    // ── chat (7 character) ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ChatIsLiveInShippedTable()
    {
        Assert.True(AccountSyncDescriptors.ForType(7)!.ImplementedByProxy);
        Assert.Equal("v1/classic-era-42597/chat/character/elwynn/2361-0/7", AccountSyncKeys.Build("Elwynn", Guid, 7));
    }

    [Fact]
    public void ChatOnlyCapabilityUploadsOnlyChat()
    {
        OnlyFamilyUploads("chat", 7, character: true);
    }

    // ── cuf (RawCuf, no type) ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void CufIsLiveInShippedTable()
    {
        var cuf = AccountSyncDescriptors.All.Single(d => d.Family == "cuf");
        Assert.True(cuf.ImplementedByProxy);
        Assert.True(cuf.ImplementedByLauncher);
        Assert.Equal(2048, cuf.MaxBytes);
        Assert.Equal("cuf.bin", cuf.FileName);
    }

    // ── tts (8 account / 9 character) ────────────────────────────────────────────────────────────────

    [Fact]
    public void TtsStaysDarkInShippedTable()
    {
        Assert.False(AccountSyncDescriptors.ForType(8)!.ImplementedByProxy);
        Assert.False(AccountSyncDescriptors.ForType(9)!.ImplementedByProxy);
        Assert.Null(AccountSyncKeys.Build("Elwynn", Guid, 8));
        Assert.Null(AccountSyncKeys.Build("Elwynn", Guid, 9));
    }

    [Fact]
    public void TtsOnlyCapabilityUploadsOnlyTts()
    {
        OnlyFamilyUploads("tts", 8, character: false);
    }

    // ── flagged (10 account / 11 character) ──────────────────────────────────────────────────────────

    [Fact]
    public void FlaggedStaysDarkInShippedTable()
    {
        Assert.False(AccountSyncDescriptors.ForType(10)!.ImplementedByProxy);
        Assert.False(AccountSyncDescriptors.ForType(11)!.ImplementedByProxy);
        Assert.Null(AccountSyncKeys.Build("Elwynn", Guid, 10));
        Assert.Null(AccountSyncKeys.Build("Elwynn", Guid, 11));
    }

    [Fact]
    public void FlaggedOnlyCapabilityUploadsOnlyFlagged()
    {
        OnlyFamilyUploads("flagged", 10, character: false);
    }

    // ── clickbindings (12 character) ─────────────────────────────────────────────────────────────────

    [Fact]
    public void ClickbindingsStaysDarkInShippedTable()
    {
        Assert.False(AccountSyncDescriptors.ForType(12)!.ImplementedByProxy);
        Assert.Null(AccountSyncKeys.Build("Elwynn", Guid, 12));
    }

    [Fact]
    public void ClickbindingsOnlyCapabilityUploadsOnlyClickbindings()
    {
        OnlyFamilyUploads("clickbindings", 12, character: true);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>With the family's switch on and bindings off, only the family's document is uploaded:
    /// the bindings file present on disk must not move.</summary>
    private static void OnlyFamilyUploads(string family, uint type, bool character)
    {
        string directory = NewDirectory();
        File.WriteAllBytes(Path.Combine(directory, "data-2.bin"), Container(2));
        string fileName = character ? $"data-{type}-{Guid.GetLowValue()}-{Guid.GetHighValue()}.bin" : $"data-{type}.bin";
        File.WriteAllBytes(Path.Combine(directory, fileName), character ? Container(type, Guid.GetLowValue(), Guid.GetHighValue()) : Container(type));
        var handler = new FakeHandler((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath switch
        {
            var p when p.EndsWith("/capabilities") => Json(Capabilities($"{{\"bindings\":false,\"{family}\":true}}")),
            var p when p.EndsWith("/documents") => Json("{\"documents\":[]}"),
            _ => Json("{}"),
        }));
        using var service = new AccountSyncService(Options(), new HttpClient(handler), descriptors: WithFamilies(family));

        service.ReconcileAtWorldEntry(directory, "Elwynn", Guid);
        service.FlushPending(TimeSpan.FromSeconds(3));

        // The scope is part of the key: a character family uploaded as "account" (or the reverse) must be
        // caught, which is exactly what the placebo's wrong-scope descriptor row does.
        string expectedSegment = $"/{family}/{(character ? "character" : "account")}/";
        var requests = WaitForPut(handler, family);
        Assert.Contains(requests, r => r.Method == HttpMethod.Put && r.Path.Contains(expectedSegment, StringComparison.Ordinal));
        Assert.DoesNotContain(requests, r => r.Path.Contains("/bindings/", StringComparison.Ordinal));
    }

    /// <summary>The world entry queues and the worker uploads on its own thread; give it a moment before
    /// reading the request list, so the snapshot is not taken mid-flight.</summary>
    private static (HttpMethod Method, string Path)[] WaitForPut(FakeHandler handler, string family)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            var snapshot = handler.Snapshot();
            if (snapshot.Any(r => r.Method == HttpMethod.Put && r.Path.Contains($"/{family}/", StringComparison.Ordinal)))
                return snapshot;
            Thread.Sleep(20);
        }
        return handler.Snapshot();
    }

    private static byte[] Container(uint type, ulong low = 0, ulong high = 0)
    {
        byte[] content = Encoding.ASCII.GetBytes($"synthetic {type}\r\n");
        byte[] compressed;
        using (var ms = new MemoryStream())
        {
            using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(content);
            compressed = ms.ToArray();
        }
        var bytes = new byte[36 + compressed.Length];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, low);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8), high);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(16), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), type);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28), (uint)content.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(32), compressed.Length);
        compressed.CopyTo(bytes.AsSpan(36));
        return bytes;
    }

    private static string Capabilities(string families) => $"{{\"mode\":\"on\",\"families\":{families},\"apiVersion\":1}}";

    private static string NewDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "jimsproxy-account-sync-tests", System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private static AccountSyncOptions Options() => new()
    {
        Enabled = true,
        BaseUri = new Uri("https://example.test/api/"),
        Token = "token",
        DeviceId = "12345678-1234-1234-1234-123456789abc",
        DeviceLabel = "test",
    };

    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path)> Requests { get; } = new();

        public (HttpMethod Method, string Path)[] Snapshot()
        {
            lock (Requests) return Requests.ToArray();
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests) Requests.Add((request.Method, request.RequestUri!.AbsolutePath));
            return respond(request, cancellationToken);
        }
    }
}
