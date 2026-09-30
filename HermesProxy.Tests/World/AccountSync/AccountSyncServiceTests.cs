using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HermesProxy.World;
using HermesProxy.World.Server;
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
    public void ReconcileAtWorldEntry_BudgetExpiresAfterPayloadRead_DoesNotWriteLocally()
    {
        string directory = WithChangedLocalFile(123);
        string file = Path.Combine(directory, "data-2.bin");
        byte[] local = File.ReadAllBytes(file);
        var journal = new AccountSyncJournal();
        journal.Documents[Key] = new() { Revision = "r1", Sha256 = AccountSyncContainer.Sha256(local) };
        journal.Save(Path.Combine(directory, "sync-journal.json"));
        string remoteHash = AccountSyncContainer.Sha256(AccountSyncContainerTests.RealData);
        using var service = Service(new FakeHandler(request => request.RequestUri!.AbsolutePath switch
        {
            var p when p.EndsWith("/capabilities") => Json(HttpStatusCode.OK, Capabilities()),
            var p when p.EndsWith("/documents") => Json(HttpStatusCode.OK, ListHead("r2", remoteHash)),
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new DelayedReadStream(
                    Encoding.UTF8.GetBytes(Document("r2", remoteHash, AccountSyncContainerTests.RealData)),
                    TimeSpan.FromMilliseconds(1600))),
            },
        }));

        service.ReconcileAtWorldEntry(directory, "Elwynn", Guid);

        Assert.Equal(local, File.ReadAllBytes(file));
        Assert.False(Directory.Exists(Path.Combine(directory, "sync-backups")));
        Assert.Equal("r1", AccountSyncJournal.Load(Path.Combine(directory, "sync-journal.json")).Documents[Key].Revision);
    }

    [Fact]
    public void ReconcileAtWorldEntry_LocalChangesDuringPayloadRequest_AbortsCompareAndSwap()
    {
        string directory = WithChangedLocalFile(123);
        string file = Path.Combine(directory, "data-2.bin");
        byte[] original = File.ReadAllBytes(file);
        byte[] newer = ChangedData(456);
        var journal = new AccountSyncJournal();
        journal.Documents[Key] = new() { Revision = "r1", Sha256 = AccountSyncContainer.Sha256(original) };
        journal.Save(Path.Combine(directory, "sync-journal.json"));
        string remoteHash = AccountSyncContainer.Sha256(AccountSyncContainerTests.RealData);
        using var service = Service(new FakeHandler(request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/capabilities")) return Json(HttpStatusCode.OK, Capabilities());
            if (path.EndsWith("/documents")) return Json(HttpStatusCode.OK, ListHead("r2", remoteHash));
            File.WriteAllBytes(file, newer);
            return Json(HttpStatusCode.OK, Document("r2", remoteHash, AccountSyncContainerTests.RealData));
        }));

        service.ReconcileAtWorldEntry(directory, "Elwynn", Guid);

        Assert.Equal(newer, File.ReadAllBytes(file));
        Assert.False(Directory.Exists(Path.Combine(directory, "sync-backups")));
        Assert.Equal("r1", AccountSyncJournal.Load(Path.Combine(directory, "sync-journal.json")).Documents[Key].Revision);
    }

    [Fact]
    public async Task Restore_SaveStartedWhileFinalLockIsHeld_WaitsThenWins()
    {
        string account = "sync-lock-test-" + System.Guid.NewGuid().ToString("N");
        var manager = new AccountDataManager(account, "Elwynn") { Data = new AccountData[8] };
        string directory = manager.GetAccountDataDirectory();
        string accountDirectory = Directory.GetParent(directory)!.FullName;
        string file = Path.Combine(directory, "data-2.bin");
        byte[] local = ChangedData(123);
        byte[] saved = ChangedData(789);
        BinaryPrimitives.WriteUInt64LittleEndian(saved.AsSpan(0), Guid.GetLowValue());
        BinaryPrimitives.WriteUInt64LittleEndian(saved.AsSpan(8), Guid.GetHighValue());
        File.WriteAllBytes(file, local);
        var journal = new AccountSyncJournal();
        journal.Documents[Key] = new() { Revision = "r1", Sha256 = AccountSyncContainer.Sha256(local) };
        journal.Save(Path.Combine(directory, "sync-journal.json"));
        string remoteHash = AccountSyncContainer.Sha256(AccountSyncContainerTests.RealData);
        using var restoreHasLock = new ManualResetEventSlim();
        using var allowRestore = new ManualResetEventSlim();
        using var saveStarted = new ManualResetEventSlim();
        using var service = Service(new FakeHandler(request => request.RequestUri!.AbsolutePath switch
        {
            var p when p.EndsWith("/capabilities") => Json(HttpStatusCode.OK, Capabilities()),
            var p when p.EndsWith("/documents") => Json(HttpStatusCode.OK, ListHead("r2", remoteHash)),
            _ => Json(HttpStatusCode.OK, Document("r2", remoteHash, AccountSyncContainerTests.RealData)),
        }));
        service.RestoreLockAcquiredForTest = () =>
        {
            restoreHasLock.Set();
            allowRestore.Wait(TestContext.Current.CancellationToken);
        };

        try
        {
            Task restore = Task.Run(
                () => service.ReconcileAtWorldEntry(directory, "Elwynn", Guid), TestContext.Current.CancellationToken);
            Assert.True(restoreHasLock.Wait(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
            Task save = Task.Run(() =>
            {
                saveStarted.Set();
                int compressedLength = BinaryPrimitives.ReadInt32LittleEndian(saved.AsSpan(32));
                manager.SaveData(Guid, 789, 2,
                    BinaryPrimitives.ReadUInt32LittleEndian(saved.AsSpan(28)), saved.AsSpan(36, compressedLength).ToArray());
            }, TestContext.Current.CancellationToken);
            Assert.True(saveStarted.Wait(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
            await Task.Delay(50, TestContext.Current.CancellationToken);
            Assert.False(save.IsCompleted);

            allowRestore.Set();
            await Task.WhenAll(restore, save);

            Assert.Equal(saved, File.ReadAllBytes(file));
        }
        finally
        {
            allowRestore.Set();
            if (Directory.Exists(accountDirectory)) Directory.Delete(accountDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Restore_CancelledImmediatelyAfterTempWrite_DeletesTempFile()
    {
        string directory = WithChangedLocalFile(123);
        string file = Path.Combine(directory, "data-2.bin");
        byte[] local = File.ReadAllBytes(file);
        var journal = new AccountSyncJournal();
        journal.Documents[Key] = new() { Revision = "r1", Sha256 = AccountSyncContainer.Sha256(local) };
        journal.Save(Path.Combine(directory, "sync-journal.json"));
        string remoteHash = AccountSyncContainer.Sha256(AccountSyncContainerTests.RealData);
        using var cancellation = new CancellationTokenSource();
        using var service = Service(new FakeHandler(request => request.RequestUri!.AbsolutePath switch
        {
            var p when p.EndsWith("/capabilities") => Json(HttpStatusCode.OK, Capabilities()),
            var p when p.EndsWith("/documents") => Json(HttpStatusCode.OK, ListHead("r2", remoteHash)),
            _ => Json(HttpStatusCode.OK, Document("r2", remoteHash, AccountSyncContainerTests.RealData)),
        }));
        service.RestoreTempWrittenForTest = cancellation.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.ReconcileAtWorldEntryAsync(directory, "Elwynn", Guid, cancellation.Token));

        Assert.False(File.Exists(file + ".sync.tmp"));
        Assert.Equal(local, File.ReadAllBytes(file));
    }

    [Fact]
    public async Task Client_ResponseBodiesHaveEndpointSpecificHardCaps()
    {
        var declaredStream = new NonSeekableReadStream(Encoding.UTF8.GetBytes(Capabilities()));
        var handler = new FakeHandler(request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            HttpContent content;
            if (path.EndsWith("/capabilities"))
            {
                content = new StreamContent(declaredStream);
                content.Headers.ContentLength = 64 * 1024 + 1;
            }
            else if (path.EndsWith("/documents") && request.Method == HttpMethod.Get)
                content = new StreamContent(new NonSeekableReadStream(new byte[1024 * 1024 + 1]));
            else if (request.Method == HttpMethod.Get)
                content = new StreamContent(new NonSeekableReadStream(new byte[256 * 1024 + 1]));
            else
                content = new StreamContent(new NonSeekableReadStream(new byte[64 * 1024 + 1]));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        var client = new AccountSyncClient(Options(), new HttpClient(handler));
        CancellationToken token = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetCapabilitiesAsync(token));
        Assert.Equal(0, declaredStream.BytesRead);
        await Assert.ThrowsAsync<InvalidDataException>(() => client.ListDocumentsAsync(token));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetDocumentAsync(Key, token));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.PutDocumentAsync(
            Key, AccountSyncContainerTests.RealData, AccountSyncContainer.Sha256(AccountSyncContainerTests.RealData),
            null, System.Guid.NewGuid().ToString("D"), token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Upload_RetryReusesClientUploadId(bool conflictCandidate)
    {
        string directory = WithLocalFile();
        var clock = new FakeClock();
        var uploadIds = new ConcurrentQueue<string>();
        var handler = new FakeHandler(async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/capabilities"))
                return Json(HttpStatusCode.OK, Capabilities());
            if (request.Method == HttpMethod.Get)
                return Json(HttpStatusCode.OK, conflictCandidate
                    ? ListHead("r1", new string('a', 64))
                    : EmptyList());

            string requestJson = await request.Content!.ReadAsStringAsync(token);
            uploadIds.Enqueue(JsonDocument.Parse(requestJson).RootElement.GetProperty("clientUploadId").GetString()!);
            return uploadIds.Count == 1
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                : Json(HttpStatusCode.OK, Head("r2", AccountSyncContainer.Sha256(AccountSyncContainerTests.RealData)));
        });
        using var service = new AccountSyncService(Options(), new HttpClient(handler), clock);

        await service.ReconcileUploadForTestAsync(directory, "Elwynn", Guid, 2, TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => clock.Delays.Count >= 1);
        clock.Advance(TimeSpan.FromSeconds(30));
        await WaitUntilAsync(() => uploadIds.Count == 2);

        string[] ids = uploadIds.ToArray();
        Assert.Equal(ids[0], ids[1]);
        Assert.All(ids, id => Assert.True(System.Guid.TryParseExact(id, "D", out _)));
        Assert.All(handler.Requests.Where(x => x.Method is not null && x.Method != HttpMethod.Get),
            request => Assert.Equal(conflictCandidate ? HttpMethod.Post : HttpMethod.Put, request.Method));
    }

    [Fact]
    public async Task Upload_TransientFailuresRetryWithBackoffThenStop()
    {
        string directory = WithLocalFile();
        var clock = new FakeClock();
        HttpStatusCode[] statuses =
        [
            HttpStatusCode.InternalServerError,
            HttpStatusCode.TooManyRequests,
            HttpStatusCode.ServiceUnavailable,
            HttpStatusCode.BadGateway,
        ];
        int puts = 0;
        var handler = new FakeHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/capabilities")) return Json(HttpStatusCode.OK, Capabilities());
            if (request.Method == HttpMethod.Get) return Json(HttpStatusCode.OK, EmptyList());
            return new HttpResponseMessage(statuses[puts++]);
        });
        using var service = new AccountSyncService(Options(), new HttpClient(handler), clock);

        await service.ReconcileUploadForTestAsync(directory, "Elwynn", Guid, 2, TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => clock.Delays.Count >= 1);
        Assert.Equal(TimeSpan.FromSeconds(30), clock.Delays[0]);
        clock.Advance(TimeSpan.FromSeconds(30));
        await WaitUntilAsync(() => clock.Delays.Count >= 2);
        Assert.Equal(TimeSpan.FromMinutes(2), clock.Delays[1]);
        clock.Advance(TimeSpan.FromMinutes(2));
        await WaitUntilAsync(() => clock.Delays.Count >= 3);
        Assert.Equal(TimeSpan.FromMinutes(10), clock.Delays[2]);
        clock.Advance(TimeSpan.FromMinutes(10));
        await WaitUntilAsync(() => Volatile.Read(ref puts) == 4);
        clock.Advance(TimeSpan.FromDays(1));
        await Task.Yield();

        Assert.Equal(4, puts);
        Assert.Equal(3, clock.Delays.Count);
    }

    [Fact]
    public async Task Upload_NewerSaveReplacesPendingRetry()
    {
        string directory = WithLocalFile();
        string file = Path.Combine(directory, "data-2.bin");
        byte[] newer = ChangedData(789);
        string newerHash = AccountSyncContainer.Sha256(newer);
        var clock = new FakeClock();
        var uploadIds = new ConcurrentQueue<string>();
        int puts = 0;
        var handler = new FakeHandler(async (request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/capabilities")) return Json(HttpStatusCode.OK, Capabilities());
            if (request.Method == HttpMethod.Get) return Json(HttpStatusCode.OK, EmptyList());
            string requestJson = await request.Content!.ReadAsStringAsync(TestContext.Current.CancellationToken);
            using JsonDocument requestBody = JsonDocument.Parse(requestJson);
            uploadIds.Enqueue(requestBody.RootElement.GetProperty("clientUploadId").GetString()!);
            puts++;
            if (puts == 1) return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            string hash = requestBody.RootElement.GetProperty("sha256").GetString()!;
            return Json(HttpStatusCode.OK, Head("r2", hash));
        });
        using var service = new AccountSyncService(Options(), new HttpClient(handler), clock);

        await service.ReconcileUploadForTestAsync(directory, "Elwynn", Guid, 2, TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => clock.Delays.Count >= 1);
        File.WriteAllBytes(file, newer);
        service.EnqueueSaved(directory, "Elwynn", Guid, 2);
        await WaitUntilAsync(() => clock.Delays.Contains(TimeSpan.FromSeconds(5)));
        clock.Advance(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => AccountSyncJournal.Load(Path.Combine(directory, "sync-journal.json")).Documents.ContainsKey(Key));

        Assert.Equal(newerHash, AccountSyncJournal.Load(Path.Combine(directory, "sync-journal.json")).Documents[Key].Sha256);
        Assert.Equal(2, puts);
        Assert.NotEqual(uploadIds.ElementAt(0), uploadIds.ElementAt(1));
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

    private static string WithChangedLocalFile(long timestamp)
    {
        string directory = NewDirectory();
        File.WriteAllBytes(Path.Combine(directory, "data-2.bin"), ChangedData(timestamp));
        return directory;
    }

    private static byte[] ChangedData(long timestamp)
    {
        byte[] data = (byte[])AccountSyncContainerTests.RealData.Clone();
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(16), timestamp);
        return data;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 200 && !condition(); attempt++)
            await Task.Delay(5, TestContext.Current.CancellationToken);
        Assert.True(condition(), "condition was not reached");
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

    private class NonSeekableReadStream : Stream
    {
        private readonly byte[] _bytes;
        private int _offset;

        public NonSeekableReadStream(byte[] bytes) => _bytes = bytes;
        public int BytesRead => _offset;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _offset; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = Math.Min(count, _bytes.Length - _offset);
            _bytes.AsSpan(_offset, read).CopyTo(buffer.AsSpan(offset, read));
            _offset += read;
            return read;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = Math.Min(buffer.Length, _bytes.Length - _offset);
            _bytes.AsMemory(_offset, read).CopyTo(buffer);
            _offset += read;
            return ValueTask.FromResult(read);
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class DelayedReadStream : NonSeekableReadStream
    {
        private readonly TimeSpan _delay;
        private bool _delayed;

        public DelayedReadStream(byte[] bytes, TimeSpan delay) : base(bytes) => _delay = delay;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_delayed)
            {
                _delayed = true;
                await Task.Delay(_delay, CancellationToken.None);
            }
            return await base.ReadAsync(buffer, CancellationToken.None);
        }
    }

    private sealed class FakeClock : IAccountSyncClock
    {
        private readonly object _gate = new();
        private readonly List<(DateTimeOffset Due, TaskCompletionSource Completion)> _waiting = new();
        private readonly List<TimeSpan> _delays = new();
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;

        public DateTimeOffset UtcNow { get { lock (_gate) return _now; } }
        public IReadOnlyList<TimeSpan> Delays { get { lock (_gate) return _delays.ToArray(); } }

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                _delays.Add(delay);
                _waiting.Add((_now + delay, completion));
            }
            cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
            return completion.Task;
        }

        public void Advance(TimeSpan duration)
        {
            List<TaskCompletionSource> ready;
            lock (_gate)
            {
                _now += duration;
                ready = _waiting.Where(item => item.Due <= _now).Select(item => item.Completion).ToList();
                _waiting.RemoveAll(item => item.Due <= _now);
            }
            foreach (var completion in ready) completion.TrySetResult();
        }
    }
}
