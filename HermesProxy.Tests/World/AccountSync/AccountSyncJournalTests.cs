using System;
using System.IO;
using HermesProxy.World.Server.AccountSync;
using Xunit;

namespace HermesProxy.Tests.World.AccountSync;

public class AccountSyncJournalTests
{
    [Fact]
    public void SaveLoad_RoundTripsAndLeavesNoTempFile()
    {
        string directory = NewDirectory();
        string path = Path.Combine(directory, "sync-journal.json");
        var journal = new AccountSyncJournal();
        journal.Documents["key"] = new() { Revision = "r12", Sha256 = "abc" };

        journal.Save(path);
        var loaded = AccountSyncJournal.Load(path);

        Assert.Equal("r12", loaded.Documents["key"].Revision);
        Assert.Equal("abc", loaded.Documents["key"].Sha256);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"version\":2,\"documents\":{}}")]
    public void Load_CorruptOrUnknownVersion_ReturnsEmpty(string contents)
    {
        string path = Path.Combine(NewDirectory(), "sync-journal.json");
        File.WriteAllText(path, contents);
        Assert.Empty(AccountSyncJournal.Load(path).Documents);
    }

    private static string NewDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "jimsproxy-account-sync-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
