using System.Text.Json;
using System.Text.Json.Serialization;

namespace HermesProxy.World.Server.AccountSync;

/// <summary>Last-confirmed remote revisions for one account and realm.</summary>
public sealed class AccountSyncJournal
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Journal schema version.</summary>
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    /// <summary>Last-confirmed remote revisions, indexed by canonical document key. Only ever holds a
    /// revision the server confirmed; a mere attempt never appears here (K2 B1).</summary>
    [JsonPropertyName("documents")]
    public Dictionary<string, AccountSyncJournalEntry> Documents { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Unix milliseconds of the last reconciliation attempt per document key, 0/absent when
    /// never attempted. Kept apart from <see cref="Documents"/> so the decision table never mistakes an
    /// attempt for a confirmed revision (K2 B1). An older proxy ignores this field.</summary>
    [JsonPropertyName("attempts")]
    public Dictionary<string, long> Attempts { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Reads a valid v1 journal, returning an empty journal for missing or invalid input.</summary>
    public static AccountSyncJournal Load(string path) => Load(path, out _);

    /// <summary>Reads a valid v1 journal and says what it found. The returned journal is the same empty one in
    /// every failure case, so the decision table stays conservative (a file that differs from the server becomes
    /// a conflict candidate, never an overwrite); the status lets the caller tell the normal first run
    /// (<see cref="AccountSyncJournalLoad.Missing"/>) from a journal that was there and could not be used
    /// (<see cref="AccountSyncJournalLoad.Unreadable"/>, <see cref="AccountSyncJournalLoad.Foreign"/>), which is a
    /// defect worth a log line and its own conflict reason.</summary>
    public static AccountSyncJournal Load(string path, out AccountSyncJournalLoad status)
    {
        try
        {
            if (!File.Exists(path))
            {
                status = AccountSyncJournalLoad.Missing;
                return new();
            }
            var journal = JsonSerializer.Deserialize<AccountSyncJournal>(File.ReadAllBytes(path), JsonOptions);
            if (journal is not { Version: 1, Documents: not null })
            {
                status = AccountSyncJournalLoad.Foreign;
                return new();
            }
            journal.Attempts ??= new(StringComparer.Ordinal);
            status = AccountSyncJournalLoad.Loaded;
            return journal;
        }
        catch (Exception)
        {
            status = AccountSyncJournalLoad.Unreadable;
            return new();
        }
    }

    /// <summary>Atomically replaces the journal on disk.</summary>
    public void Save(string path) => SaveWithinBudget(path, CancellationToken.None);

    /// <summary>Starts each synchronous journal write only while the caller's budget remains active.</summary>
    public void SaveWithinBudget(string path, CancellationToken cancellationToken) =>
        SaveWithinBudget(path, cancellationToken, null);

    internal void SaveWithinBudget(string path, CancellationToken cancellationToken, Action? afterTempWrite)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".tmp";
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(this, JsonOptions));
            afterTempWrite?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temp, path, true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}

/// <summary>What <see cref="AccountSyncJournal.Load(string, out AccountSyncJournalLoad)"/> found on disk.</summary>
public enum AccountSyncJournalLoad
{
    /// <summary>A valid v1 journal was read.</summary>
    Loaded,
    /// <summary>There is no journal file: the normal state on the first run of a PC (or after the folder was deleted).</summary>
    Missing,
    /// <summary>The file exists but could not be read or parsed (damaged, truncated, locked).</summary>
    Unreadable,
    /// <summary>The file parsed but is not a v1 journal (another version or no document table).</summary>
    Foreign,
}

/// <summary>A last-confirmed remote revision and content hash.</summary>
public sealed class AccountSyncJournalEntry
{
    [JsonPropertyName("revision")]
    public string Revision { get; set; } = "";
    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = "";
    /// <summary>Hash of the confirmed content without the container's time field
    /// (<see cref="AccountSyncContainer.ContentSha256"/>); absent in journals written before 2026-10-02.</summary>
    [JsonPropertyName("contentSha256")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ContentSha256 { get; set; }
}
