using HermesProxy.World.Server.AccountSync;
using Xunit;

namespace HermesProxy.Tests.World.AccountSync;

public class AccountSyncDecisionTests
{
    private static AccountSyncJournalEntry Journal(string revision = "r1", string hash = "a") => new() { Revision = revision, Sha256 = hash };
    private static AccountSyncDocumentHead Remote(string revision = "r1", string hash = "a", string state = "active") =>
        new() { ActiveRevision = revision, Sha256 = hash, State = state };

    [Theory]
    [InlineData(null, false, AccountSyncAction.Nothing)]
    [InlineData(null, true, AccountSyncAction.Restore)]
    [InlineData("a", false, AccountSyncAction.UploadCreate)]
    [InlineData("a", true, AccountSyncAction.Adopt)]
    [InlineData("b", true, AccountSyncAction.ConflictCandidate)]
    public void Decide_NoJournalRows_ReturnsExpected(string? local, bool hasRemote, AccountSyncAction expected)
    {
        Assert.Equal(expected, AccountSyncDecision.Decide(local, null, hasRemote ? Remote() : null, AccountSyncMode.On));
    }

    [Fact]
    public void Decide_LocalAndRemoteEqualJournal_ReturnsNothing() =>
        Assert.Equal(AccountSyncAction.Nothing, AccountSyncDecision.Decide("a", Journal(), Remote(), AccountSyncMode.On));

    [Fact]
    public void Decide_UnchangedLocalAndNewRemote_ReturnsRestore() =>
        Assert.Equal(AccountSyncAction.Restore, AccountSyncDecision.Decide("a", Journal(), Remote("r2", "b"), AccountSyncMode.On));

    [Fact]
    public void Decide_ChangedLocalAndJournalRemote_ReturnsConditionalUpload() =>
        Assert.Equal(AccountSyncAction.UploadMatch, AccountSyncDecision.Decide("b", Journal(), Remote(), AccountSyncMode.On));

    [Fact]
    public void Decide_ChangedLocalMatchesNewRemote_ReturnsAdopt() =>
        Assert.Equal(AccountSyncAction.Adopt, AccountSyncDecision.Decide("b", Journal(), Remote("r2", "b"), AccountSyncMode.On));

    [Fact]
    public void Decide_BothChangedDifferently_ReturnsConflictCandidate() =>
        Assert.Equal(AccountSyncAction.ConflictCandidate, AccountSyncDecision.Decide("b", Journal(), Remote("r2", "c"), AccountSyncMode.On));

    [Fact]
    public void Decide_UploadOnlyTurnsRestoreIntoNothing() =>
        Assert.Equal(AccountSyncAction.Nothing, AccountSyncDecision.Decide(null, null, Remote(), AccountSyncMode.UploadOnly));

    [Fact]
    public void Decide_UploadOnlyTurnsNewerRemoteRestoreIntoNothing() =>
        Assert.Equal(AccountSyncAction.Nothing, AccountSyncDecision.Decide("a", Journal(), Remote("r2", "b"), AccountSyncMode.UploadOnly));

    // The client re-stamps some documents without changing them (chat, type 7, on every logout; measured
    // 2026-10-02). Local hash "b" differs from the journal's "a" only by the time field: content hash "c" on both.
    private static AccountSyncJournalEntry JournalWithContent(string revision = "r1") =>
        new() { Revision = revision, Sha256 = "a", ContentSha256 = "c" };

    [Fact]
    public void Decide_RestampedLocalAndJournalRemote_ReturnsNothing() =>
        Assert.Equal(AccountSyncAction.Nothing, AccountSyncDecision.Decide("b", JournalWithContent(), Remote(), AccountSyncMode.On, "c"));

    [Fact]
    public void Decide_RestampedLocalAndNewRemote_ReturnsRestore() =>
        Assert.Equal(AccountSyncAction.Restore, AccountSyncDecision.Decide("b", JournalWithContent(), Remote("r2", "d"), AccountSyncMode.On, "c"));

    [Fact]
    public void Decide_ChangedContentAndJournalRemote_StillUploads() =>
        Assert.Equal(AccountSyncAction.UploadMatch, AccountSyncDecision.Decide("b", JournalWithContent(), Remote(), AccountSyncMode.On, "e"));

    [Fact]
    public void Decide_JournalWithoutContentHash_KeepsTheOldRule() =>
        Assert.Equal(AccountSyncAction.UploadMatch, AccountSyncDecision.Decide("b", Journal(), Remote(), AccountSyncMode.On, "c"));

    [Fact]
    public void Decide_ConflictHeadBlocksNormalWork() =>
        Assert.Equal(AccountSyncAction.ConflictBlocked, AccountSyncDecision.Decide("a", Journal(), Remote(state: "conflict"), AccountSyncMode.On));
}
