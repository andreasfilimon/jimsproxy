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

    /// <summary>Entries indexed by canonical document key.</summary>
    [JsonPropertyName("documents")]
    public Dictionary<string, AccountSyncJournalEntry> Documents { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Reads a valid v1 journal, returning an empty journal for missing or invalid input.</summary>
    public static AccountSyncJournal Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new();
            var journal = JsonSerializer.Deserialize<AccountSyncJournal>(File.ReadAllBytes(path), JsonOptions);
            return journal is { Version: 1, Documents: not null } ? journal : new();
        }
        catch
        {
            return new();
        }
    }

    /// <summary>Atomically replaces the journal on disk.</summary>
    public void Save(string path) => SaveWithinBudget(path, CancellationToken.None);

    /// <summary>Atomically replaces the journal on disk unless cancellation has been requested.</summary>
    public void SaveWithinBudget(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".tmp";
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(this, JsonOptions));
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temp, path, true);
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested && File.Exists(temp)) File.Delete(temp);
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
}
