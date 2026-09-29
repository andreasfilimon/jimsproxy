using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HermesProxy.World.Server.AccountSync;

/// <summary>HTTP client for the StoneTavern account-sync v1 API.</summary>
public sealed class AccountSyncClient
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;
    private readonly AccountSyncOptions _options;

    /// <summary>Creates a reusable API client over an injected HttpClient.</summary>
    public AccountSyncClient(AccountSyncOptions options, HttpClient http)
    {
        _options = options;
        _http = http;
    }

    /// <summary>Gets and validates the server capability document.</summary>
    public async Task<AccountSyncCapabilities?> GetCapabilitiesAsync(CancellationToken cancellationToken)
    {
        using var timeout = RequestToken(cancellationToken);
        using var response = await SendAsync(HttpMethod.Get, "capabilities", null, timeout.Token);
        if (!response.IsSuccessStatusCode) throw new AccountSyncHttpException(response.StatusCode);
        return await response.Content.ReadFromJsonAsync<AccountSyncCapabilities>(JsonOptions, timeout.Token);
    }

    /// <summary>Lists all document heads visible to the token.</summary>
    public async Task<IReadOnlyList<AccountSyncDocumentHead>?> ListDocumentsAsync(CancellationToken cancellationToken)
    {
        using var timeout = RequestToken(cancellationToken);
        using var response = await SendAsync(HttpMethod.Get, "documents", null, timeout.Token);
        if (!response.IsSuccessStatusCode) throw new AccountSyncHttpException(response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AccountSyncDocumentList>(JsonOptions, timeout.Token))?.Documents;
    }

    /// <summary>Gets one document payload, returning null for 404.</summary>
    public async Task<AccountSyncDocument?> GetDocumentAsync(string key, CancellationToken cancellationToken)
    {
        using var timeout = RequestToken(cancellationToken);
        using var response = await SendAsync(HttpMethod.Get, $"documents/{key}", null, timeout.Token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode) throw new AccountSyncHttpException(response.StatusCode);
        return await response.Content.ReadFromJsonAsync<AccountSyncDocument>(JsonOptions, timeout.Token);
    }

    /// <summary>Creates or conditionally updates a document.</summary>
    public Task<AccountSyncWriteResult> PutDocumentAsync(string key, byte[] payload, string hash, string? revision,
        CancellationToken cancellationToken) => WriteAsync(HttpMethod.Put, $"documents/{key}", payload, hash, revision, cancellationToken);

    /// <summary>Posts local content as a conflict candidate.</summary>
    public Task<AccountSyncWriteResult> PostConflictCandidateAsync(string key, byte[] payload, string hash,
        CancellationToken cancellationToken) => WriteAsync(HttpMethod.Post, $"documents/{key}/conflict-candidates", payload, hash, null, cancellationToken);

    private async Task<AccountSyncWriteResult> WriteAsync(HttpMethod method, string path, byte[] payload, string hash,
        string? revision, CancellationToken cancellationToken)
    {
        using var timeout = RequestToken(cancellationToken);
        var body = new AccountSyncUpload
        {
            Sha256 = hash,
            PayloadB64 = Convert.ToBase64String(payload),
            ClientUploadId = Guid.NewGuid().ToString("D"),
            DeviceId = _options.DeviceId,
            DeviceLabel = _options.DeviceLabel,
        };
        using var request = CreateRequest(method, path);
        request.Content = JsonContent.Create(body, options: JsonOptions);
        if (method == HttpMethod.Put)
        {
            if (revision == null) request.Headers.TryAddWithoutValidation("If-None-Match", "*");
            else request.Headers.TryAddWithoutValidation("If-Match", revision);
        }
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        AccountSyncDocumentHead? head = null;
        if (response.IsSuccessStatusCode)
            head = await response.Content.ReadFromJsonAsync<AccountSyncDocumentHead>(JsonOptions, timeout.Token);
        return new AccountSyncWriteResult(response.StatusCode, head);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(method, path);
        request.Content = content;
        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, new Uri(_options.BaseUri!, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.Token);
        if (_options.LauncherVersion != null)
            request.Headers.TryAddWithoutValidation("X-Stonetavern-Launcher-Version", _options.LauncherVersion);
        return request;
    }

    private static CancellationTokenSource RequestToken(CancellationToken callerToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        source.CancelAfter(RequestTimeout);
        return source;
    }
}

/// <summary>Capabilities returned by the sync API.</summary>
public sealed class AccountSyncCapabilities
{
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "off";
    [JsonPropertyName("families")]
    public Dictionary<string, bool> Families { get; set; } = new();
    [JsonPropertyName("apiVersion")]
    public int ApiVersion { get; set; }

    /// <summary>Returns the usable bindings mode, or Off for an incompatible response.</summary>
    public AccountSyncMode GetBindingsMode()
    {
        if (ApiVersion != 1 || !Families.TryGetValue("bindings", out bool enabled) || !enabled) return AccountSyncMode.Off;
        return Mode switch { "on" => AccountSyncMode.On, "upload-only" => AccountSyncMode.UploadOnly, _ => AccountSyncMode.Off };
    }
}

internal sealed class AccountSyncDocumentList
{
    [JsonPropertyName("documents")]
    public List<AccountSyncDocumentHead> Documents { get; set; } = new();
}

/// <summary>Remote metadata used during reconciliation.</summary>
public class AccountSyncDocumentHead
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = "";
    [JsonPropertyName("activeRevision")]
    public string? ActiveRevision { get; set; }
    [JsonPropertyName("revision")]
    public string? RevisionValue { get; set; }
    [JsonIgnore]
    public string Revision => ActiveRevision ?? RevisionValue ?? "";
    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = "";
    [JsonPropertyName("byteLength")]
    public int ByteLength { get; set; }
    [JsonPropertyName("state")]
    public string State { get; set; } = "active";
}

/// <summary>Remote document including its base64 payload.</summary>
public sealed class AccountSyncDocument : AccountSyncDocumentHead
{
    [JsonPropertyName("payloadB64")]
    public string PayloadB64 { get; set; } = "";
}

internal sealed class AccountSyncUpload
{
    [JsonPropertyName("formatVersion")]
    public int FormatVersion { get; set; } = 1;
    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = "";
    [JsonPropertyName("payloadB64")]
    public string PayloadB64 { get; set; } = "";
    [JsonPropertyName("clientUploadId")]
    public string ClientUploadId { get; set; } = "";
    [JsonPropertyName("deviceId")]
    public string DeviceId { get; set; } = "";
    [JsonPropertyName("deviceLabel")]
    public string DeviceLabel { get; set; } = "";
}

/// <summary>Status and optional head returned by a write.</summary>
public sealed record AccountSyncWriteResult(HttpStatusCode StatusCode, AccountSyncDocumentHead? Head);

/// <summary>Non-success response from a payload request.</summary>
public sealed class AccountSyncHttpException(HttpStatusCode statusCode) : Exception
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}
