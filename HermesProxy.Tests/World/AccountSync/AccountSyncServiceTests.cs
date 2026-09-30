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

    /// <summary>Found 2026-09-30: stopping the worker completed its debounce wait, and it then took the pending
    /// upload and ran it with the cancelled token, so the shutdown flush found nothing to send.</summary>
    [Fact]
    public async Task Worker_StoppedWhileWaiting_LeavesThePendingUploadToTheFlush()
    {
        string directory = WithLocalFile();
        int puts = 0;
        var clock = new FakeClock();
        var handler = new FakeHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/capabilities")) return Json(HttpStatusCode.OK, Capabilities());
            if (request.Method == HttpMethod.Get) return Json(HttpStatusCode.OK, EmptyList());
            Interlocked.Increment(ref puts);
            return Json(HttpStatusCode.OK, Head("r1", AccountSyncContainer.Sha256(AccountSyncContainerTests.RealData)));
        });
        using var service = new AccountSyncService(Options(), new HttpClient(handler), clock);
        service.EnqueueSaved(directory, "Elwynn", Guid, 2);
        await WaitUntilAsync(() => clock.Delays.Count >= 1); // the worker waits out the debounce
        service.StopWorkerForTest();
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.Equal(1, service.PendingCountForTest);
        Assert.Equal(0, Volatile.Read(ref puts));
        service.FlushPending(TimeSpan.FromSeconds(2));
        Assert.Equal(1, Volatile.Read(ref puts));
    }

    /// <summary>Stage D round 2: shutdown lands between the worker taking an upload and running it. The upload
    /// must still reach the server in the shutdown flush.</summary>
    [Fact]
    public async Task Shutdown_WhileTheWorkerHoldsAnUpload_StillSendsIt()
    {
        string directory = WithLocalFile();
        int puts = 0;
        var clock = new FakeClock();
        var handler = new FakeHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/capabilities")) return Json(HttpStatusCode.OK, Capabilities());
            if (request.Method == HttpMethod.Get) return Json(HttpStatusCode.OK, EmptyList());
            Interlocked.Increment(ref puts);
            return Json(HttpStatusCode.OK, Head("r1", AccountSyncContainer.Sha256(AccountSyncContainerTests.RealData)));
        });
        using var service = new AccountSyncService(Options(), new HttpClient(handler), clock);
        using var took = new ManualResetEventSlim();
        // Held until shutdown cancels, then a little longer, so a flush that does not wait for the worker
        // would certainly run first and find the queue empty.
        service.WorkerTookItemForTest = stop => { took.Set(); stop.WaitHandle.WaitOne(TimeSpan.FromSeconds(5)); Thread.Sleep(200); };
        service.EnqueueSaved(directory, "Elwynn", Guid, 2);
        await WaitUntilAsync(() => clock.Delays.Count >= 1);
        clock.Advance(TimeSpan.FromSeconds(5)); // debounce over: the worker takes the upload and is held there
        Assert.True(took.Wait(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken), "positive control: the worker took it");

        service.Shutdown(TimeSpan.FromSeconds(2));

        Assert.Equal(1, Volatile.Read(ref puts));
    }

    /// <summary>Both shutdown paths (normal stop and process exit) call the flush; the second call must do nothing.</summary>
    [Fact]
    public async Task FlushForShutdown_CalledTwice_FlushesOnce()
    {
        string directory = WithLocalFile();
        int puts = 0;
        var handler = new FakeHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/capabilities")) return Json(HttpStatusCode.OK, Capabilities());
            if (request.Method == HttpMethod.Get) return Json(HttpStatusCode.OK, EmptyList());
            Interlocked.Increment(ref puts);
            return Json(HttpStatusCode.OK, Head("r1", AccountSyncContainer.Sha256(AccountSyncContainerTests.RealData)));
        });
        using var service = new AccountSyncService(Options(), new HttpClient(handler), new FakeClock());
        try
        {
            AccountSyncRuntime.UseForTest(service);
            service.EnqueueSaved(directory, "Elwynn", Guid, 2);
            AccountSyncRuntime.FlushForShutdown();
            int afterFirst = Volatile.Read(ref puts);
            Assert.Equal(1, afterFirst); // positive control: the first call uploads the pending save
            service.EnqueueSaved(directory, "Elwynn", Guid, 2); // ignored: the service is shut down
            AccountSyncRuntime.FlushForShutdown();
            await Task.Delay(50, TestContext.Current.CancellationToken);
            Assert.Equal(afterFirst, Volatile.Read(ref puts));
        }
        finally
        {
            AccountSyncRuntime.UseForTest(null);
        }
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

    private static FileStream HoldLauncherLock(string directory) => new(
        Path.Combine(directory, AccountSyncDirectoryLock.FileName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    [Fact]
    public void ReconcileAtWorldEntry_LauncherHoldsTheLockFile_LeavesTheFileForNextTime()
    {
        string directory = NewDirectory();
        byte[] local = ChangedData(123);
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

        service.RestoreLockWait = TimeSpan.FromMilliseconds(100); // well inside the budget, so the lock decides

        using (HoldLauncherLock(directory))
            service.ReconcileAtWorldEntry(directory, "Elwynn", Guid);

        Assert.Equal(local, File.ReadAllBytes(Path.Combine(directory, "data-2.bin")));
        Assert.False(Directory.Exists(Path.Combine(directory, "sync-backups")));
        Assert.Equal("r1", AccountSyncJournal.Load(Path.Combine(directory, "sync-journal.json")).Documents[Key].Revision);
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Fact]
    public void ReconcileAtWorldEntry_LauncherHoldsTheLock_WorldEntryStaysInsideItsBudget()
    {
        string directory = NewDirectory();
        byte[] local = ChangedData(123);
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
        Assert.True(service.RestoreLockWait > TimeSpan.FromMilliseconds(1500), "positive control: the lock wait alone is longer than the budget");

        var clock = Stopwatch.StartNew();
        using (HoldLauncherLock(directory))
            service.ReconcileAtWorldEntry(directory, "Elwynn", Guid);

        Assert.True(clock.Elapsed < TimeSpan.FromMilliseconds(1650), $"world entry waited {clock.ElapsedMilliseconds} ms (budget 1500 ms)");
        Assert.Equal(local, File.ReadAllBytes(Path.Combine(directory, "data-2.bin")));
    }

    /// <summary>The launcher opens a file of exactly this name; renaming it on one side silently ends the lock.</summary>
    [Fact]
    public void DirectoryLock_FileNameIsTheOneTheLauncherUses() =>
        Assert.Equal(".st-sync.lock", AccountSyncDirectoryLock.FileName);

    [Fact]
    public async Task SaveData_WhileTheLauncherHoldsTheLock_WaitsThenSaves()
    {
        string account = "sync-lock-test-" + System.Guid.NewGuid().ToString("N");
        var manager = new AccountDataManager(account, "Elwynn") { Data = new AccountData[8] };
        string directory = manager.GetAccountDataDirectory();
        string accountDirectory = Directory.GetParent(directory)!.FullName;
        byte[] saved = ChangedData(789);
        int compressedLength = BinaryPrimitives.ReadInt32LittleEndian(saved.AsSpan(32));
        try
        {
            Task save;
            using (HoldLauncherLock(directory))
            {
                save = Task.Run(() => manager.SaveData(Guid, 789, 2,
                    BinaryPrimitives.ReadUInt32LittleEndian(saved.AsSpan(28)), saved.AsSpan(36, compressedLength).ToArray()),
                    TestContext.Current.CancellationToken);
                await Task.Delay(300, TestContext.Current.CancellationToken);
                Assert.False(save.IsCompleted, "the save must wait while the launcher holds the lock");
                Assert.False(File.Exists(Path.Combine(directory, "data-2.bin")));
            }
            await save.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.True(File.Exists(Path.Combine(directory, "data-2.bin")));
            Assert.True(File.Exists(Path.Combine(directory, AccountSyncDirectoryLock.FileName)), "the lock file is never deleted");
        }
        finally
        {
            if (Directory.Exists(accountDirectory)) Directory.Delete(accountDirectory, recursive: true);
        }
    }

    private static (AccountDataManager Manager, string Directory, string AccountDirectory) NewManager()
    {
        string account = "sync-lock-test-" + System.Guid.NewGuid().ToString("N");
        var manager = new AccountDataManager(account, "Elwynn") { Data = new AccountData[8] };
        string directory = manager.GetAccountDataDirectory();
        return (manager, directory, Directory.GetParent(directory)!.FullName);
    }

    /// <summary>What SaveData writes for this GUID: the container with the GUID in its header.</summary>
    private static byte[] Saved(long timestamp)
    {
        byte[] data = ChangedData(timestamp);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(0), Guid.GetLowValue());
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(8), Guid.GetHighValue());
        return data;
    }

    private static void Save(AccountDataManager manager, byte[] container)
    {
        int compressedLength = BinaryPrimitives.ReadInt32LittleEndian(container.AsSpan(32));
        manager.SaveData(Guid, BinaryPrimitives.ReadInt64LittleEndian(container.AsSpan(16)), 2,
            BinaryPrimitives.ReadUInt32LittleEndian(container.AsSpan(28)), container.AsSpan(36, compressedLength).ToArray());
    }

    /// <summary>Round 1 finding: the launcher holds the lock past the proxy's wait (it stalled before its move),
    /// the client saves meanwhile, then the launcher writes its older bytes. The client's save must win.</summary>
    [Fact]
    public async Task SaveData_LauncherStallsPastTheWait_TheClientSaveStillWinsAfterTheLaunchersMove()
    {
        var (manager, directory, accountDirectory) = NewManager();
        string file = Path.Combine(directory, "data-2.bin");
        byte[] launcherBytes = ChangedData(111);
        byte[] clientSave = Saved(999);
        try
        {
            using (var launcher = HoldLauncherLock(directory))
            {
                var clock = Stopwatch.StartNew();
                await Task.Run(() => Save(manager, clientSave), TestContext.Current.CancellationToken);
                Assert.True(clock.Elapsed >= AccountSyncDirectoryLock.SaveWait - TimeSpan.FromMilliseconds(100));
                Assert.False(File.Exists(file), "the final file must not be written past the lock");
                Assert.True(AccountSyncDeferredSaves.IsPending(file));
                Assert.Equal(999, manager.LoadData(Guid, 2)!.Timestamp); // the pending save is what the client gets back
                File.WriteAllBytes(file, launcherBytes); // the launcher's own move, still under its lock
            }
            await WaitUntilAsync(() => !AccountSyncDeferredSaves.IsPending(file), TimeSpan.FromSeconds(3));
            Assert.Equal(clientSave, File.ReadAllBytes(file));
            Assert.False(File.Exists(AccountSyncDeferredSaves.PathFor(file)));
        }
        finally
        {
            if (Directory.Exists(accountDirectory)) Directory.Delete(accountDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task SaveData_ANewerSaveWithTheLock_SupersedesTheDeferredOne()
    {
        var (manager, directory, accountDirectory) = NewManager();
        string file = Path.Combine(directory, "data-2.bin");
        try
        {
            using (HoldLauncherLock(directory))
                await Task.Run(() => Save(manager, Saved(500)), TestContext.Current.CancellationToken);
            Assert.True(AccountSyncDeferredSaves.IsPending(file) || File.Exists(file), "positive control: the first save was deferred or already applied");
            Save(manager, Saved(600));
            // Right away, before the background thread runs: the older deferred save is gone and not read.
            Assert.False(AccountSyncDeferredSaves.IsPending(file));
            Assert.Equal(600, manager.LoadData(Guid, 2)!.Timestamp);
            await Task.Delay(AccountSyncDeferredSaves.RetryInterval * 3, TestContext.Current.CancellationToken);
            Assert.Equal(Saved(600), File.ReadAllBytes(file));
            Assert.False(AccountSyncDeferredSaves.IsPending(file));
            Assert.False(File.Exists(AccountSyncDeferredSaves.PathFor(file)));
        }
        finally
        {
            if (Directory.Exists(accountDirectory)) Directory.Delete(accountDirectory, recursive: true);
        }
    }

    /// <summary>Round 2 finding 4: after a crash the player's newest save is what the next world entry sees.</summary>
    [Fact]
    public void DeferredSaves_LeftByADeadProcess_ArePutInPlaceAndTheOldFileIsBackedUp()
    {
        string directory = NewDirectory();
        string file = Path.Combine(directory, "data-2.bin");
        byte[] current = ChangedData(300);
        byte[] leftover = ChangedData(700);
        File.WriteAllBytes(file, current);
        File.WriteAllBytes(AccountSyncDeferredSaves.PathFor(file), leftover);

        AccountSyncDeferredSaves.RecoverLeftovers(directory, DateTimeOffset.FromUnixTimeMilliseconds(1_727_000_000_000));

        Assert.Equal(leftover, File.ReadAllBytes(file));
        Assert.False(File.Exists(AccountSyncDeferredSaves.PathFor(file)));
        string kept = Assert.Single(Directory.GetFiles(Path.Combine(directory, "sync-backups")));
        Assert.Equal("data-2.bin.1727000000000.bak", Path.GetFileName(kept));
        Assert.Equal(current, File.ReadAllBytes(kept));
    }

    /// <summary>Round 2 finding 1: a crash after the temp write but before its rename must not lose the save.</summary>
    [Fact]
    public void DeferredSaves_AWholeTempFileFromACrash_WinsOverAnOlderDeferredOne()
    {
        string directory = NewDirectory();
        string file = Path.Combine(directory, "data-2.bin");
        File.WriteAllBytes(file, ChangedData(100));
        File.WriteAllBytes(AccountSyncDeferredSaves.PathFor(file), ChangedData(200));
        File.WriteAllBytes(file + AccountSyncDeferredSaves.TempSuffix, ChangedData(300));

        AccountSyncDeferredSaves.RecoverLeftovers(directory, DateTimeOffset.FromUnixTimeMilliseconds(1_727_000_000_000));

        Assert.Equal(ChangedData(300), File.ReadAllBytes(file));
        var kept = Directory.GetFiles(Path.Combine(directory, "sync-backups")).Select(File.ReadAllBytes).ToList();
        Assert.Equal(2, kept.Count);
        Assert.Contains(kept, k => k.SequenceEqual(ChangedData(100)));
        Assert.Contains(kept, k => k.SequenceEqual(ChangedData(200)));
        Assert.Empty(Directory.GetFiles(directory, "*.deferred*"));
    }

    [Fact]
    public void DeferredSaves_ACutOffTempFile_IsKeptButNeverPutInPlace()
    {
        string directory = NewDirectory();
        string file = Path.Combine(directory, "data-2.bin");
        byte[] current = ChangedData(100);
        File.WriteAllBytes(file, current);
        byte[] cut = ChangedData(300)[..40];
        File.WriteAllBytes(file + AccountSyncDeferredSaves.TempSuffix, cut);
        Assert.False(AccountSyncDeferredSaves.IsWholeContainer(file + AccountSyncDeferredSaves.TempSuffix)); // positive control

        AccountSyncDeferredSaves.RecoverLeftovers(directory, DateTimeOffset.FromUnixTimeMilliseconds(1_727_000_000_000));

        Assert.Equal(current, File.ReadAllBytes(file));
        Assert.Equal(cut, File.ReadAllBytes(Assert.Single(Directory.GetFiles(Path.Combine(directory, "sync-backups")))));
    }

    /// <summary>Round 2 finding 2: an older save that was held up before any lock wakes up after a newer one.</summary>
    [Fact]
    public async Task SaveData_AnOlderSaveThatWakesUpLate_NeverReplacesANewerOne()
    {
        var (manager, directory, accountDirectory) = NewManager();
        string file = Path.Combine(directory, "data-2.bin");
        using var olderSequenced = new ManualResetEventSlim();
        using var releaseOlder = new ManualResetEventSlim();
        AccountDataManager.SaveSequencedForTest = timestamp =>
        {
            if (timestamp != 100) return;
            olderSequenced.Set();
            releaseOlder.Wait(TimeSpan.FromSeconds(5));
        };
        try
        {
            var older = Task.Run(() => Save(manager, Saved(100)), TestContext.Current.CancellationToken);
            Assert.True(olderSequenced.Wait(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
            Save(manager, Saved(200));
            releaseOlder.Set();
            await older;
            Assert.Equal(Saved(200), File.ReadAllBytes(file));
        }
        finally
        {
            AccountDataManager.SaveSequencedForTest = null;
            releaseOlder.Set();
            if (Directory.Exists(accountDirectory)) Directory.Delete(accountDirectory, recursive: true);
        }
    }

    /// <summary>Round 2 finding 3: with the file lock in hand, a busy gate must not hold a restore past its wait.</summary>
    [Fact]
    public async Task DirectoryLock_BusyGateAfterTheFileLock_GivesUpInTimeAndLetsTheFileGo()
    {
        string directory = NewDirectory();
        object gate = AccountSyncDirectoryLock.For(directory);
        using var gateHeld = new ManualResetEventSlim();
        using var releaseGate = new ManualResetEventSlim();
        var holder = new Thread(() => { lock (gate) { gateHeld.Set(); releaseGate.Wait(TimeSpan.FromSeconds(10)); } });
        holder.Start();
        try
        {
            Assert.True(gateHeld.Wait(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
            var clock = Stopwatch.StartNew();
            await Task.Run(() =>
            {
                using var held = AccountSyncDirectoryLock.Enter(directory, TimeSpan.FromMilliseconds(300), gateWait: TimeSpan.FromMilliseconds(300));
                Assert.False(held.HasFileLock);
            }, TestContext.Current.CancellationToken);
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), $"waited {clock.ElapsedMilliseconds} ms");
            HoldLauncherLock(directory).Dispose(); // the file lock was let go
        }
        finally
        {
            releaseGate.Set();
            holder.Join();
        }
    }

    [Fact]
    public async Task ReconcileAtWorldEntry_ASaveWaitingForTheLock_MakesTheRestoreStandDown()
    {
        var (manager, directory, accountDirectory) = NewManager();
        string file = Path.Combine(directory, "data-2.bin");
        byte[] local = ChangedData(123);
        File.WriteAllBytes(file, local);
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
        try
        {
            FileStream launcher = HoldLauncherLock(directory);
            await Task.Run(() => Save(manager, Saved(999)), TestContext.Current.CancellationToken);
            Assert.True(AccountSyncDeferredSaves.IsPending(file), "positive control: the save is deferred");
            launcher.Dispose();
            // The lock is free again, but the deferred save is newer than the server's copy.
            service.ReconcileAtWorldEntry(directory, "Elwynn", Guid);
            Assert.False(Directory.Exists(Path.Combine(directory, "sync-backups")), "no restore backup was made");
            await WaitUntilAsync(() => !AccountSyncDeferredSaves.IsPending(file), TimeSpan.FromSeconds(3));
            Assert.Equal(Saved(999), File.ReadAllBytes(file));
        }
        finally
        {
            if (Directory.Exists(accountDirectory)) Directory.Delete(accountDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task DirectoryLock_AWaitingWriterDoesNotHoldTheGate()
    {
        string directory = NewDirectory();
        using var launcher = HoldLauncherLock(directory);
        var waiting = Task.Run(() =>
        {
            using var held = AccountSyncDirectoryLock.Enter(directory, TimeSpan.FromSeconds(1));
        }, TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.True(Monitor.TryEnter(AccountSyncDirectoryLock.For(directory), 0), "the gate must be free while the file lock is polled");
        Monitor.Exit(AccountSyncDirectoryLock.For(directory));
        await waiting;
    }

    /// <summary>A real second process: flock(1) takes the same lock the launcher's .NET runtime takes.</summary>
    [Fact]
    public async Task DirectoryLock_AnotherProcessHoldingTheFile_IsSeen()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/flock")) return;
        string directory = NewDirectory();
        string lockFile = Path.Combine(directory, AccountSyncDirectoryLock.FileName);
        File.WriteAllBytes(lockFile, []);
        using (var holder = Process.Start(new ProcessStartInfo("/usr/bin/flock", ["-x", lockFile, "sleep", "30"]))!)
        {
            try
            {
                bool busy = false;
                for (int i = 0; i < 100 && !busy; i++)
                {
                    using var probe = AccountSyncDirectoryLock.Enter(directory, TimeSpan.Zero);
                    busy = !probe.HasFileLock;
                    if (!busy) { probe.Dispose(); await Task.Delay(20, TestContext.Current.CancellationToken); }
                }
                Assert.True(busy, "the lock held by another process must be seen");
            }
            finally
            {
                holder.Kill(entireProcessTree: true);
                await holder.WaitForExitAsync(TestContext.Current.CancellationToken);
            }
        }
        using var after = AccountSyncDirectoryLock.Enter(directory, TimeSpan.FromSeconds(2));
        Assert.True(after.HasFileLock);
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

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        while (!condition() && clock.Elapsed < timeout)
            await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.True(condition(), "condition was not reached");
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
