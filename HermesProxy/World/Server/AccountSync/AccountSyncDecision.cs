namespace HermesProxy.World.Server.AccountSync;

/// <summary>Actions produced by the account-sync reconciliation table.</summary>
public enum AccountSyncAction
{
    Nothing,
    Restore,
    UploadCreate,
    UploadMatch,
    Adopt,
    ConflictCandidate,
    ConflictBlocked,
}

/// <summary>Pure implementation of the local/journal/remote decision table.</summary>
public static class AccountSyncDecision
{
    /// <summary>Chooses the next action without performing I/O. <paramref name="localContentHash"/> is the local
    /// file's hash without the time field; when it equals the journal's, a file the client only re-stamped
    /// counts as unchanged and is not uploaded again.</summary>
    public static AccountSyncAction Decide(string? localHash, AccountSyncJournalEntry? journal,
        AccountSyncDocumentHead? remote, AccountSyncMode mode, string? localContentHash = null)
    {
        if (remote?.State == "conflict")
            return AccountSyncAction.ConflictBlocked;

        if (localHash == null)
            return remote == null || mode == AccountSyncMode.UploadOnly ? AccountSyncAction.Nothing : AccountSyncAction.Restore;

        if (journal == null)
        {
            if (remote == null) return AccountSyncAction.UploadCreate;
            return HashEquals(localHash, remote.Sha256) ? AccountSyncAction.Adopt : AccountSyncAction.ConflictCandidate;
        }

        if (remote == null)
            return AccountSyncAction.UploadCreate;

        bool localEqualsJournal = HashEquals(localHash, journal.Sha256) ||
            (localContentHash != null && !string.IsNullOrEmpty(journal.ContentSha256) && HashEquals(localContentHash, journal.ContentSha256));
        bool remoteIsJournal = remote.Revision == journal.Revision;
        if (localEqualsJournal)
        {
            if (remoteIsJournal) return AccountSyncAction.Nothing;
            return mode == AccountSyncMode.UploadOnly ? AccountSyncAction.Nothing : AccountSyncAction.Restore;
        }

        if (remoteIsJournal)
            return AccountSyncAction.UploadMatch;
        return HashEquals(localHash, remote.Sha256) ? AccountSyncAction.Adopt : AccountSyncAction.ConflictCandidate;
    }

    private static bool HashEquals(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Server-advertised synchronization mode.</summary>
public enum AccountSyncMode { Off, UploadOnly, On }
