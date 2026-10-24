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
            return SameAsRemote(localHash, localContentHash, remote) ? AccountSyncAction.Adopt : AccountSyncAction.ConflictCandidate;
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
        return SameAsRemote(localHash, localContentHash, remote) ? AccountSyncAction.Adopt : AccountSyncAction.ConflictCandidate;
    }

    /// <summary>True when the server already holds what the local file holds: the same bytes, or, when the
    /// server states its time-free hash, the same content with only the container's time field moved. The
    /// client rewrites some documents (chat) with a new time and unchanged content; comparing whole-file
    /// hashes alone turned every such rewrite into a conflict (8 of 89 open conflicts, measured 2026-10-24).
    /// A server without <c>contentSha256</c> leaves the check at the whole-file hash.</summary>
    public static bool SameAsRemote(string localHash, string? localContentHash, AccountSyncDocumentHead remote) =>
        HashEquals(localHash, remote.Sha256) ||
        (localContentHash != null && !string.IsNullOrEmpty(remote.ContentSha256) && HashEquals(localContentHash, remote.ContentSha256));

    /// <summary>The reason to send with a conflict candidate, from the row of the table that produced it:
    /// no journal entry (the file was missing, or unreadable/foreign) or an entry that the server has moved
    /// past while the local file changed too.</summary>
    public static string CandidateReason(AccountSyncJournalEntry? journalEntry, AccountSyncJournalLoad journalLoad) =>
        journalEntry != null ? AccountSyncReasons.ServerNewer
        : journalLoad is AccountSyncJournalLoad.Unreadable or AccountSyncJournalLoad.Foreign ? AccountSyncReasons.JournalUnreadable
        : AccountSyncReasons.NoJournal;

    private static bool HashEquals(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}

/// <summary>The reasons a conflict candidate is filed with. Short lower-case slugs: the server stores them as sent
/// (up to 24 characters) and shows them next to the candidate, so a stuck document can be explained later.</summary>
public static class AccountSyncReasons
{
    /// <summary>This PC has no journal entry for the document, and the server holds something different.</summary>
    public const string NoJournal = "no-journal";
    /// <summary>As <see cref="NoJournal"/>, but the journal file existed and could not be used (corrupt, unreadable or of a
    /// version this proxy does not know), so the empty journal is not "first run on this PC".</summary>
    public const string JournalUnreadable = "journal-unreadable";
    /// <summary>The journal knows an older server revision, the server has moved on, and the local file changed as well.</summary>
    public const string ServerNewer = "server-newer";
    /// <summary>An upload with <c>If-Match</c> was refused (409) because the server revision moved between listing and upload.</summary>
    public const string IfMatch = "if-match";
    /// <summary>A first upload with <c>If-None-Match: *</c> was refused (409) because another machine created the document first.</summary>
    public const string IfNoneMatch = "if-none-match";
}

/// <summary>Server-advertised synchronization mode.</summary>
public enum AccountSyncMode { Off, UploadOnly, On }
