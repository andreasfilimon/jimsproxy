using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HermesProxy.World;
using HermesProxy.World.Server.AccountSync;
using Xunit;

namespace HermesProxy.Tests.World.AccountSync;

/// <summary>The shared descriptor table must equal the JSON fixture byte for byte; a drift (e.g. a changed
/// scope) turns this red in every repo. Released 2026-10-02: bindings, macros, config, chat, cuf; tts stays
/// dormant even when the server switch enables it. Placebo: setting tts' ImplementedByProxy=true in the table
/// makes <see cref="TtsRemainsInactive"/> red.</summary>
public class AccountSyncDescriptorsTests
{
    private static readonly WowGuid128 Guid = new(2361, 0);

    [Fact]
    public void DescriptorsMatchFixture()
    {
        string name = typeof(AccountSyncDescriptors).Assembly.GetManifestResourceNames()
            .Single(n => n.EndsWith("sync-descriptors.json", StringComparison.Ordinal));
        using var stream = typeof(AccountSyncDescriptors).Assembly.GetManifestResourceStream(name)!;
        var fixture = JsonSerializer.Deserialize<FixtureFile>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        Assert.Equal(AccountSyncDescriptors.Flavor, fixture.Flavor);
        var code = AccountSyncDescriptors.All
            .Select(d => new FixtureRow(d.Family, d.Type, d.Scope, d.Format.ToString(), d.FileName, d.MaxBytes,
                d.SwitchFamily, d.ImplementedByProxy, d.ImplementedByLauncher))
            .ToList();
        Assert.Equal(fixture.Descriptors, code);
    }

    [Fact]
    public void ForFileNameFindsCharacterFile()
    {
        var macros = AccountSyncDescriptors.ForFileName("data-5-2361-0.bin");
        Assert.NotNull(macros);
        Assert.Equal("macros", macros!.Family);
        Assert.Equal(5u, macros.Type);
        Assert.Equal("bindings", AccountSyncDescriptors.ForFileName("data-2.bin")!.Family);
        Assert.Null(AccountSyncDescriptors.ForFileName("data-5-x-0.bin"));
        Assert.Null(AccountSyncDescriptors.ForFileName("data-5-02361-0.bin"));
        Assert.Null(AccountSyncDescriptors.ForFileName("data-5-2361-0"));
        Assert.Null(AccountSyncDescriptors.ForFileName("cuf-extra.bin"));
        Assert.Equal("cuf", AccountSyncDescriptors.ForFileName("cuf.bin")!.Family);
    }

    [Fact]
    public void ReleasedFamiliesAreImplemented()
    {
        var active = AccountSyncDescriptors.All
            .Where(d => d.ImplementedByProxy || d.ImplementedByLauncher)
            .Select(d => $"{d.Family}/{d.TypeSegment}")
            .ToArray();
        Assert.Equal(new[] { "bindings/2", "bindings/3", "macros/4", "macros/5", "config/0", "config/1", "chat/7", "cuf/-" }, active);
    }

    [Fact]
    public void TtsRemainsInactive()
    {
        string directory = NewDirectory();
        // A valid data-8.bin waits to be uploaded, and the server switch enables tts (a family that stays dark).
        File.WriteAllBytes(Path.Combine(directory, "data-8.bin"), Container(8));
        var handler = new FakeHandler(request => request.RequestUri!.AbsolutePath switch
        {
            var p when p.EndsWith("/capabilities") => Json("{\"mode\":\"on\",\"families\":{\"bindings\":true,\"tts\":true},\"apiVersion\":1}"),
            var p when p.EndsWith("/documents") => Json("{\"documents\":[]}"),
            _ => Json("{\"documents\":[]}"),
        });
        using var service = new AccountSyncService(Options(), new HttpClient(handler));

        service.ReconcileAtWorldEntry(directory, "Elwynn", Guid);
        service.EnqueueSaved(directory, "Elwynn", Guid, 8);
        service.FlushPending(TimeSpan.FromSeconds(2));

        Assert.Equal(0, service.PendingCountForTest);
        Assert.DoesNotContain(handler.Snapshot(), r => r.Path.Contains("tts", StringComparison.Ordinal) || r.Path.Contains("data-8", StringComparison.Ordinal));
    }

    private sealed record FixtureFile(string Flavor, List<FixtureRow> Descriptors);
    private sealed record FixtureRow(string Family, uint? Type, string Scope, string Format, string FileName,
        int MaxBytes, string SwitchFamily, bool ImplementedByProxy, bool ImplementedByLauncher);

    private static byte[] Container(uint type, byte[]? content = null)
    {
        content ??= Encoding.ASCII.GetBytes("macro test\r\n");
        byte[] compressed;
        using (var ms = new MemoryStream())
        {
            using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(content);
            compressed = ms.ToArray();
        }
        var bytes = new byte[36 + compressed.Length];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, 0);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8), 0);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(16), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), type);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28), (uint)content.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(32), compressed.Length);
        compressed.CopyTo(bytes.AsSpan(36));
        return bytes;
    }

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

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path)> Requests { get; } = new();

        /// <summary>The requests so far, copied under the lock: the upload worker keeps adding from its own thread.</summary>
        public (HttpMethod Method, string Path)[] Snapshot()
        {
            lock (Requests) return Requests.ToArray();
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests) Requests.Add((request.Method, request.RequestUri!.AbsolutePath));
            return Task.FromResult(respond(request));
        }
    }
}
