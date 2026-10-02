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

/// <summary>P2 macros (N1). Released 2026-10-02: macros is live in the shipped table, so a macros upload
/// happens when the server switch turns macros on (<see cref="MacrosIsLiveInShippedTable"/>). The real code
/// paths also run with an injected test table (N1.2), and every placeholder file is synthetic. Placebo:
/// setting ImplementedByProxy=false for macros in the shipped table turns
/// <see cref="MacrosIsLiveInShippedTable"/> red; making lastAttemptAt persist only on success turns
/// <see cref="HangingDocumentDoesNotStarveOthers"/> red.</summary>
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
    public void MacrosIsLiveInShippedTable()
    {
        string directory = NewDirectory();
        File.WriteAllBytes(Path.Combine(directory, "data-4.bin"), Container(4));
        var handler = new FakeHandler((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath switch
        {
            var p when p.EndsWith("/capabilities") => Json(Capabilities("{\"bindings\":false,\"macros\":true}")),
            var p when p.EndsWith("/documents") => Json("{\"documents\":[]}"),
            var p when p.EndsWith("/4") => Json(Head(K4, Container(4))),
            _ => Json("{}"),
        }));
        using var service = new AccountSyncService(Options(), new HttpClient(handler));

        service.ReconcileAtWorldEntry(directory, "Elwynn", Guid);
        service.EnqueueSaved(directory, "Elwynn", Guid, 4);
        service.FlushPending(TimeSpan.FromSeconds(3));

        Assert.Equal(0, service.PendingCountForTest);
        Assert.Contains(handler.Snapshot(), r => r.Method == HttpMethod.Put && r.Path.Contains("/macros/", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.Snapshot(), r => r.Path.Contains("/bindings/", StringComparison.Ordinal));
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

        Assert.Contains(handler.Snapshot(), r => r.Method == HttpMethod.Put && r.Path.Contains("/macros/", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.Snapshot(), r => r.Path.Contains("/bindings/", StringComparison.Ordinal));
    }

    [Fact]
    public void CharacterTypeWithWrongGuidIsRejected()
    {
        Assert.False(AccountSyncContainer.Validate(Container(5, 999, 0), 5, Guid, requireGuidMatch: true, maxFileSize: AccountSyncContainer.MaxFileSize));
        Assert.True(AccountSyncContainer.Validate(Container(5, 2361, 0), 5, Guid, requireGuidMatch: true, maxFileSize: AccountSyncContainer.MaxFileSize));
        // Account-scope documents carry no GUID check.
        Assert.True(AccountSyncContainer.Validate(Container(4), 4, Guid, requireGuidMatch: false, maxFileSize: AccountSyncContainer.MaxFileSize));
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
        // so it moves to the back next entry. Placebo B (persist the attempt only on success) leaves
        // this at 0 and turns the assertion red. B1: the attempt lives in Attempts, never in Documents.
        var journal = AccountSyncJournal.Load(Path.Combine(directory, "sync-journal.json"));
        Assert.True(journal.Attempts.TryGetValue(K2, out long hungAt), "the hanging document must be marked attempted");
        Assert.True(hungAt > 0, "the hanging document must be marked attempted even on timeout");
        Assert.DoesNotContain(K2, journal.Documents.Keys);
    }

    /// <summary>K2 B1: recording an attempt must never create a confirmed-revision entry. With a local
    /// file and a remote document of a different hash, the world entry decides ConflictCandidate, and
    /// Documents stays empty because nothing was confirmed. Placebo: putting the attempt back into
    /// Documents turns the DoesNotContain red.</summary>
    [Fact]
    public void AttemptMarkIsNotAConfirmedRevision()
    {
        string directory = NewDirectory();
        File.WriteAllBytes(Path.Combine(directory, "data-2.bin"), Container(2));
        byte[] remotePayload = Container(2, 111, 222);
        using var conflictPosted = new ManualResetEventSlim();
        var handler = new FakeHandler((request, _) =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/capabilities")) return Task.FromResult(Json(Capabilities("{\"bindings\":true}")));
            if (path.EndsWith("/documents"))
                return Task.FromResult(Json(Documents(new Dictionary<string, byte[]> { [K2] = remotePayload })));
            if (path.EndsWith("/conflict-candidates")) conflictPosted.Set();
            return Task.FromResult(Json("{}"));
        });
        using var service = new AccountSyncService(Options(), new HttpClient(handler));

        service.ReconcileAtWorldEntry(directory, "Elwynn", Guid);

        bool posted = conflictPosted.Wait(TimeSpan.FromSeconds(3));
        Assert.True(posted, "the different local copy is posted as a conflict candidate; requests: " + string.Join(", ", handler.Snapshot().Select(r => r.Method + " " + r.Path)));
        var journal = AccountSyncJournal.Load(Path.Combine(directory, "sync-journal.json"));
        Assert.True(journal.Attempts.TryGetValue(K2, out long attemptedAt) && attemptedAt > 0, "the attempt is recorded");
        Assert.DoesNotContain(K2, journal.Documents.Keys);
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

        /// <summary>The requests so far, copied under the lock: the upload worker keeps adding from its own thread.</summary>
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
