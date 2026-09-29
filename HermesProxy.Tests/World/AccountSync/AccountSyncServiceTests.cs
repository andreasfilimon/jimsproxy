using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HermesProxy.World;
using HermesProxy.World.Server.AccountSync;
using Xunit;

namespace HermesProxy.Tests.World.AccountSync;

public class AccountSyncServiceTests
{
    private const string Key = "v1/classic-era-42597/bindings/account/elwynn/-/2";
    private static readonly WowGuid128 Guid = new(2361, 0);

    [Fact]
    public void ReconcileAtWorldEntry_NewerRemoteRestoresExactBytesAndBacksUpLocal()
    {
        string directory = NewDirectory();
        byte[] local = (byte[])AccountSyncContainerTests.RealData.Clone();
        BinaryPrimitives.WriteInt64LittleEndian(local.AsSpan(16), 123);
        File.WriteAllBytes(Path.Combine(directory, "data-2.bin"), local);
        var journal = new AccountSyncJournal();
        journal.Documents[Key] = new() { Revision = "r1", Sha256 = AccountSyncContainer.Sha256(local) };
        journal.Save(Path.Combine(directory, "sync-journal.json"));
        string remoteHash = AccountSyncContainer.Sha256(AccountSyncContainerTests.RealData);
        using var service = Service(new FakeHandler(request => request.RequestUri!.AbsolutePath switch
        {
            var p when p.EndsWith("/capabilities") => Json(HttpStatusCode.OK, Capabilities()),
            var p when p.EndsWith("/documents") => Json(HttpStatusCode.OK, ListHead("r2", remoteHash)),
            _ => Json(HttpStatusCode.OK, Document("r2", remoteHash, AccountSyncContainerTests.RealData)),
        }));

        service.ReconcileAtWorldEntry(directory, "Elwynn", Guid);

        Assert.Equal(AccountSyncContainerTests.RealData, File.ReadAllBytes(Path.Combine(directory, "data-2.bin")));
        string backup = Assert.Single(Directory.GetFiles(Path.Combine(directory, "sync-backups")));
        Assert.Equal(local, File.ReadAllBytes(backup));
    }

    [Fact]
    public void ReconcileAtWorldEntry_ShaMismatchDoesNotWrite()
    {
        string directory = NewDirectory();
        string remoteHash = new string('0', 64);
        using var service = Service(new FakeHandler(request => request.RequestUri!.AbsolutePath switch
        {
            var p when p.EndsWith("/capabilities") => Json(HttpStatusCode.OK, Capabilities()),
            var p when p.EndsWith("/documents") => Json(HttpStatusCode.OK, ListHead("r2", remoteHash)),
            _ => Json(HttpStatusCode.OK, Document("r2", remoteHash, AccountSyncContainerTests.RealData)),
        }));

        service.ReconcileAtWorldEntry(directory, "Elwynn", Guid);

        Assert.False(File.Exists(Path.Combine(directory, "data-2.bin")));
        Assert.False(File.Exists(Path.Combine(directory, "sync-journal.json")));
    }

    [Fact]
    public void ReconcileAtWorldEntry_HandlerNeverAnswers_ReturnsWithinBudget()
    {
        string directory = NewDirectory();
        byte[] local = AccountSyncContainerTests.RealData;
        File.WriteAllBytes(Path.Combine(directory, "data-2.bin"), local);
        using var service = Service(new FakeHandler((_, token) =>
            Task.Delay(Timeout.InfiniteTimeSpan, token).ContinueWith<HttpResponseMessage>(_ => null!, token)));
        var stopwatch = Stopwatch.StartNew();

        service.ReconcileAtWorldEntry(directory, "Elwynn", Guid);

        Assert.InRange(stopwatch.Elapsed, TimeSpan.FromSeconds(1.3), TimeSpan.FromSeconds(2.5));
        Assert.Equal(local, File.ReadAllBytes(Path.Combine(directory, "data-2.bin")));
    }

    [Fact]
    public async Task Upload_UnauthorizedDisablesLaterUploads()
    {
        string directory = WithLocalFile();
        int calls = 0;
        var handler = new FakeHandler(request =>
        {
            calls++;
            if (request.RequestUri!.AbsolutePath.EndsWith("/capabilities")) return Json(HttpStatusCode.OK, Capabilities());
            if (request.Method == HttpMethod.Get) return Json(HttpStatusCode.OK, EmptyList());
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        });
        using var service = Service(handler);

        await service.ReconcileUploadForTestAsync(directory, "Elwynn", Guid, 2, TestContext.Current.CancellationToken);
        int afterFirst = calls;
        await service.ReconcileUploadForTestAsync(directory, "Elwynn", Guid, 2, TestContext.Current.CancellationToken);

        Assert.False(service.IsEnabled);
        Assert.Equal(afterFirst, calls);
    }

    [Fact]
    public async Task Upload_ConflictThenSameHashHead_Adopts()
    {
        string directory = WithLocalFile();
        string hash = AccountSyncContainer.Sha256(AccountSyncContainerTests.RealData);
        int lists = 0;
        var handler = new FakeHandler(request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/capabilities")) return Json(HttpStatusCode.OK, Capabilities());
            if (request.Method == HttpMethod.Get)
                return Json(HttpStatusCode.OK, ++lists == 1 ? EmptyList() : ListHead("r7", hash));
            return new HttpResponseMessage(HttpStatusCode.Conflict);
        });
        using var service = Service(handler);

        await service.ReconcileUploadForTestAsync(directory, "Elwynn", Guid, 2, TestContext.Current.CancellationToken);

        Assert.Equal("r7", AccountSyncJournal.Load(Path.Combine(directory, "sync-journal.json")).Documents[Key].Revision);
        Assert.DoesNotContain(handler.Requests, x => x.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task Upload_ConflictThenDifferentHash_PostsConflictCandidate()
    {
        string directory = WithLocalFile();
        int lists = 0;
        var handler = new FakeHandler(request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/capabilities")) return Json(HttpStatusCode.OK, Capabilities());
            if (request.Method == HttpMethod.Get)
                return Json(HttpStatusCode.OK, ++lists == 1 ? EmptyList() : ListHead("r7", new string('a', 64)));
            if (request.Method == HttpMethod.Post)
                return Json(HttpStatusCode.OK, Head("r7", new string('a', 64)));
            return new HttpResponseMessage(HttpStatusCode.Conflict);
        });
        using var service = Service(handler);

        await service.ReconcileUploadForTestAsync(directory, "Elwynn", Guid, 2, TestContext.Current.CancellationToken);

        Assert.Contains(handler.Requests, x => x.Method == HttpMethod.Post && x.Path.EndsWith("/conflict-candidates"));
    }

    [Fact]
    public void SyncOff_MakesNoRequestAndCreatesNoJournal()
    {
        string directory = WithLocalFile();
        var handler = new FakeHandler(_ => throw new InvalidOperationException("must not be called"));
        using var service = new AccountSyncService(new AccountSyncOptions { Enabled = false }, new HttpClient(handler));

        service.ReconcileAtWorldEntry(directory, "Elwynn", Guid);
        service.EnqueueSaved(directory, "Elwynn", Guid, 2);

        Assert.Empty(handler.Requests);
        Assert.False(File.Exists(Path.Combine(directory, "sync-journal.json")));
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

    private static string WithLocalFile()
    {
        string directory = NewDirectory();
        File.WriteAllBytes(Path.Combine(directory, "data-2.bin"), AccountSyncContainerTests.RealData);
        return directory;
    }

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
    private static string EmptyList() => "{\"documents\":[]}";
    private static string Head(string revision, string hash) =>
        $"{{\"key\":\"{Key}\",\"activeRevision\":\"{revision}\",\"sha256\":\"{hash}\",\"byteLength\":{AccountSyncContainerTests.RealData.Length},\"state\":\"active\"}}";
    private static string ListHead(string revision, string hash) => $"{{\"documents\":[{Head(revision, hash)}]}}";
    private static string Document(string revision, string hash, byte[] payload) =>
        $"{{\"revision\":\"{revision}\",\"sha256\":\"{hash}\",\"payloadB64\":\"{Convert.ToBase64String(payload)}\",\"byteLength\":{payload.Length},\"state\":\"active\"}}";

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;
        public List<(HttpMethod Method, string Path)> Requests { get; } = new();

        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : this((request, _) => Task.FromResult(respond(request))) { }
        public FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method, request.RequestUri!.AbsolutePath));
            return _respond(request, cancellationToken);
        }
    }
}
