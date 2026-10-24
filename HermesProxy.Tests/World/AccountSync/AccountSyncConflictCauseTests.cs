using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
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

/// <summary>
/// Measured 2026-10-24: 89 documents of 24 accounts sat in conflict; 8 of them differed from the server only in the
/// container's time field, and for the rest the server could not say why the proxy had filed them. These tests pin the
/// three fixes: the decision also compares the content hash against the server's head, every conflict candidate carries
/// the reason from the decision row that produced it, and a journal that was there but unusable is reported.
/// </summary>
public class AccountSyncConflictCauseTests
{
    private const string Key = "v1/classic-era-42597/bindings/account/elwynn/-/2";
    private static readonly WowGuid128 Guid = new(2361, 0);

    // --- the shared vector with the server (apps/web/lib/launcher-sync.content-hash.test.ts) ----------------------------------

    [Fact]
    public void ContentSha256_SharedVectorWithTheServer()
    {
        byte[] vector = Enumerable.Range(0, 40).Select(i => (byte)i).ToArray();
        Assert.Equal("5faa4eec3611556812c2d74b437c8c49add3f910f10063d801441f7d75cd5e3b", AccountSyncContainer.Sha256(vector));
        Assert.Equal("25a5b9ec404d1f3e3c58868ec4be40d68b8440d24ea3b29cad2a1924d5587e55", AccountSyncContainer.ContentSha256(vector));
        Assert.Equal("e7aebf577f60412f0312d442c70a1fa6148c090bf5bab404caec29482ae779e8",
            AccountSyncContainer.ContentSha256(vector.AsSpan(0, 20)));
    }

    // --- (a) the decision compares the content hash with the server's head --------------------------------------------------------

    private static AccountSyncDocumentHead HeadObj(string revision, string sha, string? content, string state = "active") =>
        new() { ActiveRevision = revision, Sha256 = sha, ContentSha256 = content, State = state };

    [Fact]
    public void Decide_NoJournal_ServerHoldsSameContentWithOtherTime_Adopts() =>
        Assert.Equal(AccountSyncAction.Adopt,
            AccountSyncDecision.Decide("full-local", null, HeadObj("r2", "full-server", "same"), AccountSyncMode.On, "same"));

    [Fact]
    public void Decide_JournalButServerMovedAndHoldsSameContent_Adopts() =>
        Assert.Equal(AccountSyncAction.Adopt,
            AccountSyncDecision.Decide("full-local",
                new AccountSyncJournalEntry { Revision = "r1", Sha256 = "old", ContentSha256 = "older" },
                HeadObj("r2", "full-server", "same"), AccountSyncMode.On, "same"));

    [Fact]
    public void Decide_ServerWithoutContentHash_BehavesAsBefore() =>
        Assert.Equal(AccountSyncAction.ConflictCandidate,
            AccountSyncDecision.Decide("full-local", null, HeadObj("r2", "full-server", null), AccountSyncMode.On, "same"));

    [Fact]
    public void Decide_ServerContentHashDiffers_StaysAConflict() =>
        Assert.Equal(AccountSyncAction.ConflictCandidate,
            AccountSyncDecision.Decide("full-local", null, HeadObj("r2", "full-server", "other"), AccountSyncMode.On, "same"));

    [Fact]
    public void Decide_NoLocalContentHash_UsesTheWholeFileHashOnly() =>
        Assert.Equal(AccountSyncAction.ConflictCandidate,
            AccountSyncDecision.Decide("full-local", null, HeadObj("r2", "full-server", "same"), AccountSyncMode.On, null));

    [Fact]
    public void Decide_ContentHashesCompareCaseInsensitively() =>
        Assert.Equal(AccountSyncAction.Adopt,
            AccountSyncDecision.Decide("full-local", null, HeadObj("r2", "full-server", "ABCDEF"), AccountSyncMode.On, "abcdef"));

    [Fact]
    public void Decide_ConflictHeadStillBlocksEvenWithEqualContent() =>
        Assert.Equal(AccountSyncAction.ConflictBlocked,
            AccountSyncDecision.Decide("full-local", null, HeadObj("r2", "full-server", "same", "conflict"), AccountSyncMode.On, "same"));

    [Fact]
    public async Task WorldEntry_NoJournal_RestampedFileOnServer_AdoptsInsteadOfFilingAConflict()
    {
        string directory = NewDirectory();
        byte[] local = Restamped(1790972886);
        File.WriteAllBytes(Path.Combine(directory, "data-2.bin"), local);
        var handler = new RecordingHandler(request => ServerWith(request, Head("r2",
            AccountSyncContainer.Sha256(AccountSyncContainerTests.RealData),
            AccountSyncContainer.ContentSha256(AccountSyncContainerTests.RealData))));
        using var service = Service(handler);

        service.ReconcileAtWorldEntry(directory, "Elwynn", Guid);
        await Task.Delay(300, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(handler.Snapshot(), r => r.Method != HttpMethod.Get);
        var journal = AccountSyncJournal.Load(Path.Combine(directory, "sync-journal.json"));
        Assert.Equal("r2", journal.Documents[Key].Revision);
        Assert.Equal(AccountSyncContainer.Sha256(AccountSyncContainerTests.RealData), journal.Documents[Key].Sha256);
        Assert.Equal(AccountSyncContainer.ContentSha256(local), journal.Documents[Key].ContentSha256);
        Assert.Equal(local, File.ReadAllBytes(Path.Combine(directory, "data-2.bin")));
    }

    [Fact]
    public async Task WorldEntry_ServerWithoutContentHash_StillFilesTheCandidate_AsBefore()
    {
        string directory = NewDirectory();
        File.WriteAllBytes(Path.Combine(directory, "data-2.bin"), Restamped(1790972886));
        var handler = new RecordingHandler(request => ServerWith(request, Head("r2",
            AccountSyncContainer.Sha256(AccountSyncContainerTests.RealData), null)));
        using var service = Service(handler);

        service.ReconcileAtWorldEntry(directory, "Elwynn", Guid);
        var post = await WaitForPostAsync(handler);

        Assert.Equal("no-journal", ReasonOf(post));
    }

    [Fact]
    public async Task Upload_ConflictThenServerHoldsSameContent_AdoptsTheServersHash()
    {
        string directory = NewDirectory();
        byte[] local = Restamped(1790972886);
        File.WriteAllBytes(Path.Combine(directory, "data-2.bin"), local);
        string serverSha = AccountSyncContainer.Sha256(AccountSyncContainerTests.RealData);
        int lists = 0;
        var handler = new RecordingHandler(request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/capabilities")) return Json(HttpStatusCode.OK, Capabilities());
            if (request.Method == HttpMethod.Get)
                return Json(HttpStatusCode.OK, ++lists == 1 ? "{\"documents\":[]}" : List(Head("r7", serverSha, AccountSyncContainer.ContentSha256(local))));
            return new HttpResponseMessage(HttpStatusCode.Conflict);
        });
        using var service = Service(handler);

        await service.ReconcileUploadForTestAsync(directory, "Elwynn", Guid, 2, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(handler.Snapshot(), r => r.Method == HttpMethod.Post);
        var entry = AccountSyncJournal.Load(Path.Combine(directory, "sync-journal.json")).Documents[Key];
        Assert.Equal("r7", entry.Revision);
        Assert.Equal(serverSha, entry.Sha256);
    }

    // --- (b) every conflict candidate carries the reason of the row that produced it ----------------------------------------------

    [Fact]
    public void CandidateReason_FollowsTheJournalState()
    {
        var entry = new AccountSyncJournalEntry { Revision = "r1", Sha256 = "a" };
        Assert.Equal("server-newer", AccountSyncDecision.CandidateReason(entry, AccountSyncJournalLoad.Loaded));
        Assert.Equal("no-journal", AccountSyncDecision.CandidateReason(null, AccountSyncJournalLoad.Missing));
        Assert.Equal("no-journal", AccountSyncDecision.CandidateReason(null, AccountSyncJournalLoad.Loaded));
        Assert.Equal("journal-unreadable", AccountSyncDecision.CandidateReason(null, AccountSyncJournalLoad.Unreadable));
        Assert.Equal("journal-unreadable", AccountSyncDecision.CandidateReason(null, AccountSyncJournalLoad.Foreign));
    }

    [Fact]
    public async Task WorldEntry_JournalKnowsAnOlderRevision_ServerMovedAndLocalChanged_ReasonServerNewer()
    {
        string directory = NewDirectory();
        File.WriteAllBytes(Path.Combine(directory, "data-2.bin"), Restamped(111, flip: true));
        var journal = new AccountSyncJournal();
        journal.Documents[Key] = new() { Revision = "r1", Sha256 = new string('a', 64) };
        journal.Save(Path.Combine(directory, "sync-journal.json"));
        var handler = new RecordingHandler(request => ServerWith(request, Head("r2", new string('b', 64), new string('c', 64))));
        using var service = Service(handler);

        service.ReconcileAtWorldEntry(directory, "Elwynn", Guid);
        var post = await WaitForPostAsync(handler);

        Assert.Equal("server-newer", ReasonOf(post));
    }

    [Fact]
    public async Task WorldEntry_NoJournalFile_ReasonNoJournal_AndNothingIsReported()
    {
        string directory = NewDirectory();
        File.WriteAllBytes(Path.Combine(directory, "data-2.bin"), Restamped(111, flip: true));
        var warnings = new List<string>();
        var handler = new RecordingHandler(request => ServerWith(request, Head("r2", new string('b', 64), new string('c', 64))));
        using var service = Service(handler);
        service.WarningForTest = warnings.Add;

        service.ReconcileAtWorldEntry(directory, "Elwynn", Guid);
        var post = await WaitForPostAsync(handler);

        Assert.Equal("no-journal", ReasonOf(post));
        Assert.Empty(warnings);
        Assert.False(File.Exists(Path.Combine(directory, "sync-journal.json.unreadable")));
    }

    [Fact]
    public async Task Upload_PutRefusedWithIfMatch_ReasonIfMatch()
    {
        string directory = NewDirectory();
        File.WriteAllBytes(Path.Combine(directory, "data-2.bin"), Restamped(111, flip: true));
        var journal = new AccountSyncJournal();
        journal.Documents[Key] = new() { Revision = "r1", Sha256 = new string('a', 64) };
        journal.Save(Path.Combine(directory, "sync-journal.json"));
        var handler = RefusingPutServer(Head("r1", new string('a', 64), new string('a', 64)), Head("r7", new string('b', 64), new string('c', 64)));
        using var service = Service(handler);

        await service.ReconcileUploadForTestAsync(directory, "Elwynn", Guid, 2, TestContext.Current.CancellationToken);

        Assert.Equal("if-match", ReasonOf(handler.Snapshot().Single(r => r.Method == HttpMethod.Post)));
    }

    [Fact]
    public async Task Upload_FirstUploadRefusedWithIfNoneMatch_ReasonIfNoneMatch()
    {
        string directory = NewDirectory();
        File.WriteAllBytes(Path.Combine(directory, "data-2.bin"), Restamped(111, flip: true));
        var handler = RefusingPutServer(null, Head("r7", new string('b', 64), new string('c', 64)));
        using var service = Service(handler);

        await service.ReconcileUploadForTestAsync(directory, "Elwynn", Guid, 2, TestContext.Current.CancellationToken);

        Assert.Equal("if-none-match", ReasonOf(handler.Snapshot().Single(r => r.Method == HttpMethod.Post)));
    }

    [Fact]
    public async Task Upload_SavedDuringPlay_NoJournalEntryAnymore_DerivesTheReasonFromTheJournalItReads()
    {
        string directory = NewDirectory();
        File.WriteAllBytes(Path.Combine(directory, "data-2.bin"), Restamped(111, flip: true));
        var handler = new RecordingHandler(request => ServerWith(request, Head("r2", new string('b', 64), new string('c', 64))));
        using var service = Service(handler);

        await service.ReconcileUploadForTestAsync(directory, "Elwynn", Guid, 2, TestContext.Current.CancellationToken);

        Assert.Equal("no-journal", ReasonOf(handler.Snapshot().Single(r => r.Method == HttpMethod.Post)));
    }

    [Fact]
    public async Task Upload_PutWithoutConflict_SendsNoReason()
    {
        string directory = NewDirectory();
        File.WriteAllBytes(Path.Combine(directory, "data-2.bin"), Restamped(111, flip: true));
        var handler = new RecordingHandler(request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/capabilities")) return Json(HttpStatusCode.OK, Capabilities());
            if (request.Method == HttpMethod.Get) return Json(HttpStatusCode.OK, "{\"documents\":[]}");
            return Json(HttpStatusCode.OK, Head("r1", new string('a', 64), null));
        });
        using var service = Service(handler);

        await service.ReconcileUploadForTestAsync(directory, "Elwynn", Guid, 2, TestContext.Current.CancellationToken);

        var put = handler.Snapshot().Single(r => r.Method == HttpMethod.Put);
        using var json = JsonDocument.Parse(put.Body);
        Assert.False(json.RootElement.TryGetProperty("reason", out _));
    }

    // --- (c) a journal that was there but unusable is reported, a missing one is not ----------------------------------------------

    [Fact]
    public void Load_ReportsWhatItFound()
    {
        string dir = NewDirectory();
        string path = Path.Combine(dir, "sync-journal.json");

        AccountSyncJournal.Load(path, out var missing);
        Assert.Equal(AccountSyncJournalLoad.Missing, missing);

        new AccountSyncJournal().Save(path);
        AccountSyncJournal.Load(path, out var loaded);
        Assert.Equal(AccountSyncJournalLoad.Loaded, loaded);

        foreach (string garbage in new[] { "not json", "", "[]", "{\"version\":1,\"documents\":" })
        {
            File.WriteAllText(path, garbage);
            var empty = AccountSyncJournal.Load(path, out var status);
            Assert.Equal(AccountSyncJournalLoad.Unreadable, status);
            Assert.Empty(empty.Documents);
        }

        foreach (string foreign in new[] { "{\"version\":2,\"documents\":{}}", "{\"version\":1,\"documents\":null}", "null" })
        {
            File.WriteAllText(path, foreign);
            var empty = AccountSyncJournal.Load(path, out var status);
            Assert.Equal(AccountSyncJournalLoad.Foreign, status);
            Assert.Empty(empty.Documents);
        }
    }

    [Fact]
    public async Task WorldEntry_UnreadableJournal_WarnsOnce_KeepsTheFile_AndFilesTheReason()
    {
        string directory = NewDirectory();
        File.WriteAllBytes(Path.Combine(directory, "data-2.bin"), Restamped(111, flip: true));
        string journalPath = Path.Combine(directory, "sync-journal.json");
        File.WriteAllText(journalPath, "{ this journal was cut off");
        var warnings = new List<string>();
        var handler = new RecordingHandler(request => ServerWith(request, Head("r2", new string('b', 64), new string('c', 64))));
        using var service = Service(handler);
        service.WarningForTest = warnings.Add;

        service.ReconcileAtWorldEntry(directory, "Elwynn", Guid);
        var post = await WaitForPostAsync(handler);

        Assert.Equal("journal-unreadable", ReasonOf(post));
        string warning = Assert.Single(warnings);
        Assert.Contains("unreadable", warning);
        Assert.Contains("journal-unreadable", warning);
        Assert.DoesNotContain(directory, warning);
        Assert.Equal("{ this journal was cut off", File.ReadAllText(journalPath + ".unreadable"));
        // the journal on disk is usable again, so the next world entry does not warn a second time
        AccountSyncJournal.Load(journalPath, out var after);
        Assert.Equal(AccountSyncJournalLoad.Loaded, after);
    }

    [Fact]
    public void WorldEntry_ForeignVersionJournal_IsReportedToo()
    {
        string directory = NewDirectory();
        string journalPath = Path.Combine(directory, "sync-journal.json");
        File.WriteAllText(journalPath, "{\"version\":2,\"documents\":{}}");
        var warnings = new List<string>();
        using var service = Service(new RecordingHandler(request => ServerWith(request, null)));
        service.WarningForTest = warnings.Add;

        service.ReconcileAtWorldEntry(directory, "Elwynn", Guid);

        Assert.Contains("not a v1 journal", Assert.Single(warnings));
        Assert.Equal("{\"version\":2,\"documents\":{}}", File.ReadAllText(journalPath + ".unreadable"));
    }

    // --- compatibility with servers and proxies that do not know the new fields -----------------------------------------------------

    [Fact]
    public async Task ListedHeadWithFieldsThisProxyDoesNotKnow_IsReadAsBefore()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK,
            "{\"documents\":[{\"key\":\"k\",\"activeRevision\":\"r3\",\"sha256\":\"s\",\"contentSha256\":\"c\",\"futureField\":{\"x\":1},\"byteLength\":4,\"state\":\"active\"}]}"));
        var client = new AccountSyncClient(Options(), new HttpClient(handler));

        var heads = await client.ListDocumentsAsync(TestContext.Current.CancellationToken);

        var head = Assert.Single(heads!);
        Assert.Equal("c", head.ContentSha256);
        Assert.Equal("r3", head.Revision);
    }

    [Fact]
    public async Task ListedHeadFromAnOlderServer_HasNoContentHash()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK,
            "{\"documents\":[{\"key\":\"k\",\"activeRevision\":\"r3\",\"sha256\":\"s\",\"byteLength\":4,\"state\":\"active\"}]}"));
        var client = new AccountSyncClient(Options(), new HttpClient(handler));

        var head = Assert.Single((await client.ListDocumentsAsync(TestContext.Current.CancellationToken))!);

        Assert.Null(head.ContentSha256);
    }

    // --- helpers ------------------------------------------------------------------------------------------------------------------

    private static byte[] Restamped(long time, bool flip = false)
    {
        byte[] data = (byte[])AccountSyncContainerTests.RealData.Clone();
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(16), time);
        if (flip) data[0] ^= 0x01; // account scope: the GUID half is not checked, the file stays valid
        return data;
    }

    private static string ReasonOf((HttpMethod Method, string Path, string Body) post)
    {
        Assert.Equal(HttpMethod.Post, post.Method);
        Assert.EndsWith("/conflict-candidates", post.Path);
        using var json = JsonDocument.Parse(post.Body);
        return json.RootElement.GetProperty("reason").GetString()!;
    }

    private static async Task<(HttpMethod Method, string Path, string Body)> WaitForPostAsync(RecordingHandler handler)
    {
        for (int i = 0; i < 400; i++)
        {
            var post = handler.Snapshot().Where(r => r.Method == HttpMethod.Post).ToArray();
            if (post.Length > 0) return post[0];
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        Assert.Fail("no conflict candidate was posted");
        return default;
    }

    /// <summary>A server whose PUT is refused with 409; the first list is the state the proxy decided on, the next the state after the race.</summary>
    private static RecordingHandler RefusingPutServer(string? first, string afterRace)
    {
        int lists = 0;
        return new RecordingHandler(request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/capabilities")) return Json(HttpStatusCode.OK, Capabilities());
            if (request.Method == HttpMethod.Get)
                return Json(HttpStatusCode.OK, ++lists == 1 ? (first == null ? "{\"documents\":[]}" : List(first)) : List(afterRace));
            if (request.Method == HttpMethod.Post) return Json(HttpStatusCode.OK, afterRace);
            return new HttpResponseMessage(HttpStatusCode.Conflict);
        });
    }

    private static HttpResponseMessage ServerWith(HttpRequestMessage request, string? head)
    {
        string path = request.RequestUri!.AbsolutePath;
        if (path.EndsWith("/capabilities")) return Json(HttpStatusCode.OK, Capabilities());
        if (request.Method == HttpMethod.Get && path.EndsWith("/documents"))
            return Json(HttpStatusCode.OK, head == null ? "{\"documents\":[]}" : List(head));
        return Json(HttpStatusCode.OK, head ?? "{}");
    }

    private static AccountSyncService Service(HttpMessageHandler handler) => new(Options(), new HttpClient(handler));

    private static AccountSyncOptions Options() => new()
    {
        Enabled = true,
        BaseUri = new Uri("https://example.test/api/"),
        Token = "token",
        DeviceId = "12345678-1234-1234-1234-123456789abc",
        DeviceLabel = "test",
    };

    private static string NewDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "jimsproxy-account-sync-tests", System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private static string Capabilities() => "{\"mode\":\"on\",\"families\":{\"bindings\":true},\"apiVersion\":1}";

    private static string Head(string revision, string sha, string? content)
    {
        string contentField = content == null ? "" : $",\"contentSha256\":\"{content}\"";
        return $"{{\"key\":\"{Key}\",\"activeRevision\":\"{revision}\",\"sha256\":\"{sha}\"{contentField},\"byteLength\":{AccountSyncContainerTests.RealData.Length},\"state\":\"active\"}}";
    }

    private static string List(string head) => $"{{\"documents\":[{head}]}}";

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        private readonly List<(HttpMethod Method, string Path, string Body)> _requests = [];

        public (HttpMethod Method, string Path, string Body)[] Snapshot()
        {
            lock (_requests) return _requests.ToArray();
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (_requests) _requests.Add((request.Method, request.RequestUri!.AbsolutePath, body));
            return respond(request);
        }
    }
}
