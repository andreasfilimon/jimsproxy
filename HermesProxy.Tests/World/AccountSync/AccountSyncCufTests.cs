using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HermesProxy.World;
using HermesProxy.World.Server;
using HermesProxy.World.Server.AccountSync;
using Xunit;

namespace HermesProxy.Tests.World.AccountSync;

/// <summary>P5: the RawCuf document (cuf.bin) uses the same lock/defer/recover path as the container
/// documents, and its structure is validated instead of the 36-byte header. Placebo: restoring
/// RecoverLeftovers to IsWholeContainer makes the deferred CUF block look incomplete and turns
/// <see cref="CufDeferredSaveSurvivesCrash"/> red.</summary>
public class AccountSyncCufTests
{
    [Fact]
    public void CufPendingSaveIsReadFirst()
    {
        var (manager, directory, accountDirectory) = NewManager();
        string file = Path.Combine(directory, "cuf.bin");
        byte[] old = RawCufValidatorTests.CufBlock(1, 10);
        byte[] newer = RawCufValidatorTests.CufBlock(3, 40);
        File.WriteAllBytes(file, old);
        try
        {
            using (HoldLauncherLock(directory))
            {
                Task.Run(() => manager.SaveCUFProfiles(newer)).Wait();
                Assert.True(AccountSyncDeferredSaves.IsPending(file), "positive control: the save is deferred");
                Assert.Equal(newer, manager.LoadCUFProfiles());
            }
            Assert.True(WaitUntil(() => !AccountSyncDeferredSaves.IsPending(file), TimeSpan.FromSeconds(3)));
            Assert.Equal(newer, File.ReadAllBytes(file));
        }
        finally
        {
            if (Directory.Exists(accountDirectory)) Directory.Delete(accountDirectory, recursive: true);
        }
    }

    [Fact]
    public void CufDeferredSaveSurvivesCrash()
    {
        string directory = NewDirectory();
        string file = Path.Combine(directory, "cuf.bin");
        byte[] old = RawCufValidatorTests.CufBlock(1, 10);
        byte[] newer = RawCufValidatorTests.CufBlock(2, 30);
        File.WriteAllBytes(file, old);
        // A process that died between write and move leaves a whole .deferred block behind.
        File.WriteAllBytes(file + AccountSyncDeferredSaves.Suffix, newer);

        AccountSyncDeferredSaves.RecoverLeftovers(directory, DateTimeOffset.FromUnixTimeMilliseconds(1_727_000_000_000));

        Assert.Equal(newer, File.ReadAllBytes(file));
        Assert.False(File.Exists(file + AccountSyncDeferredSaves.Suffix));
        string kept = Assert.Single(Directory.GetFiles(Path.Combine(directory, "sync-backups")));
        Assert.Equal(old, File.ReadAllBytes(kept));

        // The recovered block is the local truth and is uploaded at the next world entry.
        var handler = new FakeHandler((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath switch
        {
            var p when p.EndsWith("/capabilities") => Json("{\"mode\":\"on\",\"families\":{\"cuf\":true},\"apiVersion\":1}"),
            var p when p.EndsWith("/documents") => Json("{\"documents\":[]}"),
            _ => Json("{}"),
        }));
        using var service = new AccountSyncService(Options(), new HttpClient(handler), descriptors: WithFamilies("cuf"));
        service.ReconcileAtWorldEntry(directory, "Elwynn", new WowGuid128(2361, 0));
        service.FlushPending(TimeSpan.FromSeconds(3));

        Assert.True(WaitUntil(() => handler.Snapshot().Any(r => r.Method == HttpMethod.Put && r.Path.Contains("/cuf/", StringComparison.Ordinal)), TimeSpan.FromSeconds(3)),
            "the recovered CUF block is uploaded");
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────

    private static IReadOnlyList<AccountSyncDescriptor> WithFamilies(params string[] families) =>
        AccountSyncDescriptors.All.Select(d => families.Contains(d.Family) ? d with { ImplementedByProxy = true } : d).ToArray();

    private static (AccountDataManager Manager, string Directory, string AccountDirectory) NewManager()
    {
        string account = "sync-cuf-test-" + Guid.NewGuid().ToString("N");
        var manager = new AccountDataManager(account, "Elwynn");
        string directory = manager.GetAccountDataDirectory();
        return (manager, directory, Directory.GetParent(directory)!.FullName);
    }

    private static FileStream HoldLauncherLock(string directory) => new(
        Path.Combine(directory, AccountSyncDirectoryLock.FileName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    private static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && clock.Elapsed < timeout) Thread.Sleep(20);
        return condition();
    }

    private static string NewDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "jimsproxy-account-sync-tests", Guid.NewGuid().ToString("N"));
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
        private readonly List<(HttpMethod Method, string Path)> _requests = new();

        public (HttpMethod Method, string Path)[] Snapshot()
        {
            lock (_requests) return _requests.ToArray();
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (_requests) _requests.Add((request.Method, request.RequestUri!.AbsolutePath));
            return respond(request, cancellationToken);
        }
    }
}
