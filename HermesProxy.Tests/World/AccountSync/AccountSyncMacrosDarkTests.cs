using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
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

/// <summary>P2 macros, built dark (N1). The shipped table keeps only bindings implemented, so no macros
/// request or upload happens even when the server switch turns macros on. The real code paths run with an
/// injected test table (N1.2), and every placeholder file is synthetic. Placebo: setting
/// ImplementedByProxy=true for macros in the shipped table turns <see cref="MacrosStaysDarkInShippedTable"/>
/// red; making lastAttemptAt persist only on success turns <see cref="HangingDocumentDoesNotStarveOthers"/>
/// red.</summary>
public class AccountSyncMacrosDarkTests
{
    private static readonly WowGuid128 Guid = new(2361, 0);

    private const string K2 = "v1/classic-era-42597/bindings/account/elwynn/-/2";
    private const string K3 = "v1/classic-era-42597/bindings/character/elwynn/2361-0/3";
    private const string K4 = "v1/classic-era-42597/macros/account/elwynn/-/4";
    private const string K5 = "v1/classic-era-42597/macros/character/elwynn/2361-0/5";
    private const string K0 = "v1/classic-era-42597/config/account/elwynn/-/0";
    private const string K1 = "v1/classic-era-42597/config/character/elwynn/2361-0/1";

    private static IReadOnlyList<AccountSyncDescriptor> WithFamilies(params string[] families) =>
        AccountSyncDescriptors.All
            .Select(d => families.Contains(d.Family) ? d with { ImplementedByProxy = true } : d)
            .ToArray();

    [Fact]
    public void MacrosStaysDarkInShippedTable()
    {
        string directory = NewDirectory();
        File.WriteAllBytes(Path.Combine(directory, "data-4.bin"), Container(4));
        var handler = new FakeHandler((_, _) => Task.FromResult(Json(Capabilities("{\"bindings\":false,\"macros\":true}"))));
        using var service = new AccountSyncService(Options(), new HttpClient(handler));

        service.ReconcileAtWorldEntry(directory, "Elwynn", Guid);
        service.EnqueueSaved(directory, "Elwynn", Guid, 4);
        service.FlushPending(TimeSpan.FromSeconds(2));

        Assert.Equal(0, service.PendingCountForTest);
        Assert.DoesNotContain(handler.Requests, r => r.Path.Contains("macros", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Put);
    }

    [Fact]
    public void MacrosOnlyCapabilityUploadsOnlyMacros()
    {
        string directory = NewDirectory();
        // Bindings files are present too: with the correct per-family gate they must NOT be uploaded
        // (the capability says bindings:false). Placebo A (GetMode ignores the family) makes them upload.
        File.WriteAllBytes(Path.Combine(directory, "data-2.bin"), Container(2));
        File.WriteAllBytes(Path.Combine(directory, "data-3-2361-0.bin"), Container(3, 2361, 0));
        File.WriteAllBytes(Path.Combine(directory, "data-4.bin"), Container(4));
        File.WriteAllBytes(Path.Combine(directory, "data-5-2361-0.bin"), Container(5, 2361, 0));
        var handler = new FakeHandler((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath switch
        {
            var p when p.EndsWith("/capabilities") => Json(Capabilities("{\"bindings\":false,\"macros\":true}")),
            var p when p.EndsWith("/documents") => Json("{\"documents\":[]}"),
            var p when p.EndsWith("/4") => Json(Head(K4, Container(4))),
            var p when p.EndsWith("/5") => Json(Head(K5, Container(5, 2361, 0))),
            _ => Json("{}"),
        }));
        using var service = new AccountSyncService(Options(), new HttpClient(handler), descriptors: WithFamilies("macros"));

        service.ReconcileAtWorldEntry(directory, "Elwynn", Guid);
        service.FlushPending(TimeSpan.FromSeconds(3));

        Assert.Contains(handler.Requests, r => r.Method == HttpMethod.Put && r.Path.Contains("/macros/", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.Requests, r => r.Path.Contains("/bindings/", StringComparison.Ordinal));
    }

    [Fact]
    public void CharacterTypeWithWrongGuidIsRejected()
    {
        Assert.False(AccountSyncContainer.Validate(Container(5, 999, 0), 5, Guid, AccountSyncContainer.MaxFileSize, requireGuidMatch: true));
        Assert.True(AccountSyncContainer.Validate(Container(5, 2361, 0), 5, Guid, AccountSyncContainer.MaxFileSize, requireGuidMatch: true));
        // Account-scope documents carry no GUID check.
        Assert.True(AccountSyncContainer.Validate(Container(4), 4, Guid, AccountSyncContainer.MaxFileSize, requireGuidMatch: false));
    }

    [Fact]
    public void HangingDocumentDoesNotStarveOthers()
    {
        string directory = NewDirectory();
        var payloads = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [K2] = Container(2),
            [K3] = Container(3, 2361, 0),
            [K4] = Container(4),
            [K5] = Container(5, 2361, 0),
            [K0] = Container(0),
            [K1] = Container(1, 2361, 0),
        };
        var requested = new HashSet<string>(StringComparer.Ordinal);
        var handler = new FakeHandler(async (request, token) =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/capabilities")) return Json(Capabilities("{\"bindings\":true,\"macros\":true,\"config\":true}"));
            if (path.EndsWith("/documents")) return Json(Documents(payloads));
            string key = path[(path.IndexOf("/documents/", StringComparison.Ordinal) + "/documents/".Length)..];
            lock (requested) requested.Add(key);
            if (key == K2) await Task.Delay(TimeSpan.FromSeconds(5), token);
            return Json(Document(key, payloads[key]));
        });
        using var service = new AccountSyncService(Options(), new HttpClient(handler), descriptors: WithFamilies("macros", "config"));

        var others = payloads.Keys.Where(k => k != K2).ToArray();
        for (int entry = 0; entry < 3; entry++)
        {
            var clock = Stopwatch.StartNew();
            service.ReconcileAtWorldEntry(directory, "Elwynn", Guid);
            clock.Stop();
            Assert.True(clock.Elapsed < TimeSpan.FromMilliseconds(1600), $"world entry {entry} took {clock.ElapsedMilliseconds} ms");
            lock (requested) if (others.All(requested.Contains)) break;
        }

        lock (requested)
            Assert.All(others, key => Assert.Contains(key, requested));

        // The hanging document used its sub-budget and timed out, but its attempt must still be on disk,
        // so it moves to the back next entry. Placebo B (persist lastAttemptAt only on success) leaves
        // this at 0 and turns the assertion red.
        var journal = AccountSyncJournal.Load(Path.Combine(directory, "sync-journal.json"));
        Assert.True(journal.Documents.TryGetValue(K2, out var hung), "the hanging document must be in the journal");
        Assert.True(hung.LastAttemptAt > 0, "the hanging document must be marked attempted even on timeout");
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────

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

    private static string Head(string key, byte[] payload) =>
        $"{{\"key\":\"{key}\",\"activeRevision\":\"r1\",\"sha256\":\"{AccountSyncContainer.Sha256(payload)}\",\"byteLength\":{payload.Length},\"state\":\"active\"}}";

    private static string Document(string key, byte[] payload) =>
        $"{{\"key\":\"{key}\",\"revision\":\"r1\",\"activeRevision\":\"r1\",\"sha256\":\"{AccountSyncContainer.Sha256(payload)}\",\"payloadB64\":\"{Convert.ToBase64String(payload)}\",\"byteLength\":{payload.Length},\"state\":\"active\"}}";

    private static string Documents(IReadOnlyDictionary<string, byte[]> payloads) =>
        "{\"documents\":[" + string.Join(",", payloads.Select(p => Head(p.Key, p.Value))) + "]}";

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

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests) Requests.Add((request.Method, request.RequestUri!.AbsolutePath));
            return respond(request, cancellationToken);
        }
    }
}
