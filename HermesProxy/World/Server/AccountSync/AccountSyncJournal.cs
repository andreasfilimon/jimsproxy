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
    public static AccountSyncJournal Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new();
            var journal = JsonSerializer.Deserialize<AccountSyncJournal>(File.ReadAllBytes(path), JsonOptions);
            if (journal is not { Version: 1, Documents: not null }) return new();
            journal.Attempts ??= new(StringComparer.Ordinal);
            return journal;
        }
        catch
        {
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
