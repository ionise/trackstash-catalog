using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using TrackStash.Catalog.Api.Middleware;
using TrackStash.Core.Storage;

namespace TrackStash.Catalog.Api;

/// <summary>One historical scanner record; no audio or file bytes are included.</summary>
public sealed record RemoteInventoryRecord
{
    [JsonPropertyName("media_file_id")] public long MediaFileId { get; init; }
    [JsonPropertyName("path")] public string? Path { get; init; }
    [JsonPropertyName("format")] public string? Format { get; init; }
    [JsonPropertyName("size_bytes")] public long SizeBytes { get; init; }
    [JsonPropertyName("scanned_utc")] public DateTimeOffset ScannedUtc { get; init; }
    [JsonPropertyName("last_modified_utc")] public DateTimeOffset LastModifiedUtc { get; init; }
    [JsonPropertyName("duration_seconds")] public double? DurationSeconds { get; init; }
    [JsonPropertyName("title")] public string? Title { get; init; }
    [JsonPropertyName("subtitle")] public string? Subtitle { get; init; }
    [JsonPropertyName("artist")] public string? Artist { get; init; }
    [JsonPropertyName("artists_json")] public string? ArtistsJson { get; init; }
    [JsonPropertyName("album")] public string? Album { get; init; }
    [JsonPropertyName("label")] public string? Label { get; init; }
    [JsonPropertyName("release")] public string? Release { get; init; }
    [JsonPropertyName("remixers_json")] public string? RemixersJson { get; init; }
    [JsonPropertyName("isrc")] public string? Isrc { get; init; }
    [JsonPropertyName("barcode")] public string? Barcode { get; init; }
    [JsonPropertyName("catalog_number")] public string? CatalogNumber { get; init; }
    [JsonPropertyName("track_number")] public JsonElement? TrackNumber { get; init; }
    [JsonPropertyName("disc_number")] public JsonElement? DiscNumber { get; init; }
    [JsonPropertyName("bpm")] public JsonElement? Bpm { get; init; }
    [JsonPropertyName("musical_key")] public string? MusicalKey { get; init; }
    [JsonPropertyName("genre")] public string? Genre { get; init; }
    [JsonPropertyName("year")] public long? Year { get; init; }
    [JsonPropertyName("fingerprint_raw")] public string? FingerprintRaw { get; init; }
    [JsonPropertyName("fingerprint_algorithm")] public string? FingerprintAlgorithm { get; init; }
    [JsonPropertyName("fingerprint_version")] public string? FingerprintVersion { get; init; }
    [JsonPropertyName("fingerprint_duration_seconds")] public double? FingerprintDurationSeconds { get; init; }
}

/// <summary>A bounded batch of records from a single configured scanner.</summary>
public sealed record RemoteInventoryBatchRequest
{
    public IReadOnlyList<RemoteInventoryRecord>? Items { get; init; }
}

/// <summary>Counts of saved and older records from an upload.</summary>
public sealed record RemoteInventoryBatchResponse(int Saved, int Older);
/// <summary>A browsable historical record from a remote scanner.</summary>
public sealed record RemoteInventoryItem(string OwnerId, string DeviceId, long MediaFileId, string Path, string? Title,
    string? Artist, string? Album, string? Label, string? Release, string? Format, DateTimeOffset ScannedUtc);
/// <summary>A page of remote inventory records.</summary>
public sealed record RemoteInventoryPage(IReadOnlyList<RemoteInventoryItem> Items, int Total, int Page, int PageSize);
/// <summary>Scanner identity used to select a remote inventory record.</summary>
public sealed record RemoteInventoryKey(string OwnerId, string DeviceId, long MediaFileId);
/// <summary>Remote inventory records for which matching suggestions are requested.</summary>
public sealed record RemoteMatchProposalsRequest(IReadOnlyList<RemoteInventoryKey> Items);
/// <summary>Matching suggestions based on historical scanner metadata.</summary>
public sealed record RemoteMatchProposal(RemoteInventoryKey Key, string Version, string? LinkedRecordingId,
    IReadOnlyList<MediaRecordingCandidate> Candidates, string? SuggestedRecordingId);
/// <summary>One explicitly selected catalog recording and scanner snapshot.</summary>
public sealed record RemoteMatchSelection(RemoteInventoryKey Key, string Version, string RecordingId);
/// <summary>Explicitly selected remote media links to save together.</summary>
public sealed record RemoteMatchRequest(IReadOnlyList<RemoteMatchSelection> Items);
/// <summary>Number of newly saved and already existing links.</summary>
public sealed record RemoteMatchResult(int Saved, int AlreadyLinked);

internal sealed class RemoteInventorySettings
{
    private readonly byte[]? tokenHash;
    internal string? DatabasePath { get; }
    internal string? DeviceId { get; }
    internal string? OwnerId { get; }
    internal bool Enabled => tokenHash is not null;

    internal RemoteInventorySettings(IConfiguration config, string? adminKey)
    {
        var path = config["CatalogApi:RemoteInventoryDatabasePath"];
        var device = config["CatalogApi:InventoryDeviceId"];
        var owner = config["CatalogApi:InventoryOwnerId"];
        var token = config["CatalogApi:InventoryDeviceToken"];
        if (new[] { path, device, owner, token }.All(string.IsNullOrWhiteSpace)) return;
        if (string.IsNullOrWhiteSpace(path) || !System.IO.Path.IsPathFullyQualified(path)
            || string.IsNullOrWhiteSpace(device) || device.Length > 100 || device.Any(char.IsWhiteSpace)
            || string.IsNullOrWhiteSpace(owner) || owner.Length > 100 || owner.Any(char.IsWhiteSpace)
            || string.IsNullOrWhiteSpace(token) || token.Length < 32 || token.Any(char.IsWhiteSpace)
            || token == adminKey)
            throw new InvalidOperationException("Remote inventory requires an absolute database path, device and owner IDs, and a distinct device token of at least 32 characters.");
        var full = System.IO.Path.GetFullPath(path);
        foreach (var other in new[] { config["CatalogApi:DatabasePath"], config["CatalogApi:ScannerDatabasePath"] })
            if (!string.IsNullOrWhiteSpace(other) && System.IO.Path.GetFullPath(other) == full)
                throw new InvalidOperationException("Remote inventory requires its own database, separate from the catalog and scanner databases.");
        DatabasePath = full;
        DeviceId = device;
        OwnerId = owner;
        tokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
    }

    internal bool Authenticate(HttpRequest request)
    {
        var values = request.Headers.Authorization;
        var value = values.Count == 1 ? values[0] : null;
        return tokenHash is not null && value is not null && value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            && CryptographicOperations.FixedTimeEquals(tokenHash, SHA256.HashData(Encoding.UTF8.GetBytes(value[7..])));
    }
}

internal interface IRemoteInventoryService
{
    Task<RemoteInventoryBatchResponse> SaveAsync(RemoteInventoryBatchRequest batch, CancellationToken ct);
    Task<RemoteInventoryPage> BrowseAsync(int page, string? search, CancellationToken ct);
    Task<IReadOnlyList<RemoteMatchProposal>> ProposalsAsync(RemoteMatchProposalsRequest request, CancellationToken ct);
    Task<RemoteMatchResult> MatchAsync(RemoteMatchRequest request, CancellationToken ct);
}

internal sealed partial class RemoteInventoryService(RemoteInventorySettings settings, IStorageProvider storage, TimeProvider clock) : IRemoteInventoryService
{
    private const int PageSize = 25;
    private const int ApplicationId = 0x5453494e;

    private string DatabasePath => settings.DatabasePath ?? throw new ApiRequestException(409, "remote_inventory_not_configured", "Remote inventory is not configured on the server.");

    private static bool InvalidNumericTag(JsonElement? value, bool fractional = false)
    {
        if (value is not { } tag || tag.ValueKind == JsonValueKind.Null) return false;
        return tag.ValueKind switch
        {
            JsonValueKind.String => tag.GetString()!.Length > 1000,
            JsonValueKind.Number when fractional => !tag.TryGetDouble(out var number) || !double.IsFinite(number) || number < 0,
            JsonValueKind.Number => !tag.TryGetInt64(out _),
            _ => true
        };
    }

    private static async Task CheckStore(SqliteConnection connection, SqliteTransaction transaction, bool create, CancellationToken ct)
    {
        async Task<long> Scalar(string sql)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            return Convert.ToInt64(await command.ExecuteScalarAsync(ct));
        }
        var applicationId = await Scalar("PRAGMA application_id");
        var version = await Scalar("PRAGMA user_version");
        var tables = await Scalar("SELECT count(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'");
        if (create && applicationId == 0 && version == 0 && tables == 0)
        {
            await using var schema = connection.CreateCommand();
            schema.Transaction = transaction;
            schema.CommandText = $"""
                CREATE TABLE remote_inventory (
                  owner_id TEXT NOT NULL, device_id TEXT NOT NULL, media_file_id INTEGER NOT NULL,
                  scanned_utc TEXT NOT NULL, item_json TEXT NOT NULL,
                  PRIMARY KEY(owner_id, device_id, media_file_id)
                );
                PRAGMA application_id = {ApplicationId};
                PRAGMA user_version = 1;
                """;
            await schema.ExecuteNonQueryAsync(ct);
            return;
        }
        if (applicationId != ApplicationId || version != 1
            || tables != 1 || await Scalar("SELECT count(*) FROM sqlite_master WHERE type='table' AND name='remote_inventory'") != 1)
            throw new ApiRequestException(409, "remote_inventory_invalid_store", "The configured remote inventory database is not a TrackStash remote inventory store.");
    }

    public async Task<RemoteInventoryBatchResponse> SaveAsync(RemoteInventoryBatchRequest batch, CancellationToken ct)
    {
        var items = batch.Items;
        if (items is null || items.Count is < 1 or > 100 || items.Any(i => i is null)
            || items.Select(i => i.MediaFileId).Distinct().Count() != items.Count)
            throw new ApiRequestException(400, "validation_failed", "Provide 1 to 100 distinct inventory records.");
        foreach (var item in items)
        {
            if (item.MediaFileId <= 0 || string.IsNullOrWhiteSpace(item.Path) || item.Path.Length > 4096
                || string.IsNullOrWhiteSpace(item.Format) || item.Format.Length > 50 || item.SizeBytes < 0
                || item.ScannedUtc == default || item.LastModifiedUtc == default
                || item.DurationSeconds is < 0 || item.FingerprintDurationSeconds is < 0
                || InvalidNumericTag(item.TrackNumber) || InvalidNumericTag(item.DiscNumber) || InvalidNumericTag(item.Bpm, fractional: true)
                || new[] { item.Title, item.Subtitle, item.Artist, item.Album, item.Label, item.Release, item.Isrc,
                    item.Barcode, item.CatalogNumber, item.MusicalKey, item.Genre, item.FingerprintAlgorithm,
                    item.FingerprintVersion }.Any(s => s?.Length > 1000)
                || new[] { item.ArtistsJson, item.RemixersJson }.Any(s => s?.Length > 10000)
                || item.FingerprintRaw?.Length > 800000)
                throw new ApiRequestException(400, "validation_failed", "An inventory record contains invalid or oversized fields.");
        }
        var path = DatabasePath;
        if (!Directory.Exists(System.IO.Path.GetDirectoryName(path)))
            throw new ApiRequestException(409, "remote_inventory_not_configured", "The remote inventory database directory does not exist.");
        var created = false;
        if (!File.Exists(path))
        {
            try
            {
                await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                created = true;
            }
            catch (IOException) when (File.Exists(path))
            {
                // Another uploader created the store; validate it before use.
            }
        }
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false
        }.ToString());
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await CheckStore(connection, (SqliteTransaction)transaction, created, ct);
        var saved = 0;
        foreach (var item in items)
        {
            var scanned = item.ScannedUtc.ToUniversalTime().ToString("O");
            var json = JsonSerializer.Serialize(item);
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT INTO remote_inventory(owner_id,device_id,media_file_id,scanned_utc,item_json)
                VALUES(@owner,@device,@id,@scanned,@json)
                ON CONFLICT(owner_id,device_id,media_file_id) DO UPDATE SET
                    scanned_utc=excluded.scanned_utc,item_json=excluded.item_json
                WHERE excluded.scanned_utc >= remote_inventory.scanned_utc;
                """;
            command.Parameters.AddWithValue("@owner", settings.OwnerId!);
            command.Parameters.AddWithValue("@device", settings.DeviceId!);
            command.Parameters.AddWithValue("@id", item.MediaFileId);
            command.Parameters.AddWithValue("@scanned", scanned);
            command.Parameters.AddWithValue("@json", json);
            saved += await command.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return new(saved, items.Count - saved);
    }

    public async Task<RemoteInventoryPage> BrowseAsync(int page, string? search, CancellationToken ct)
    {
        if (page < 1 || search?.Length > 200)
            throw new ApiRequestException(400, "validation_failed", "Choose page >= 1 and search <= 200 characters.");
        var path = DatabasePath;
        if (!File.Exists(path)) return new([], 0, page, PageSize);
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        await connection.OpenAsync(ct);
        const string filter = """
            (@query = '' OR instr(lower(coalesce(json_extract(item_json,'$.title'),'')),lower(@query)) > 0
             OR instr(lower(coalesce(json_extract(item_json,'$.artist'),'')),lower(@query)) > 0
             OR instr(lower(coalesce(json_extract(item_json,'$.label'),'')),lower(@query)) > 0
             OR instr(lower(coalesce(json_extract(item_json,'$.release'),'')),lower(@query)) > 0
             OR instr(lower(coalesce(json_extract(item_json,'$.album'),'')),lower(@query)) > 0)
            """;
        await using var transaction = connection.BeginTransaction(deferred: true);
        await CheckStore(connection, transaction, false, ct);
        await using var count = connection.CreateCommand();
        count.Transaction = (SqliteTransaction)transaction;
        count.CommandText = $"SELECT count(*) FROM remote_inventory WHERE {filter}";
        count.Parameters.AddWithValue("@query", search?.Trim() ?? "");
        var total = Convert.ToInt32(await count.ExecuteScalarAsync(ct));
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = $"""
            SELECT owner_id,device_id,item_json FROM remote_inventory WHERE {filter}
            ORDER BY lower(coalesce(json_extract(item_json,'$.release'),json_extract(item_json,'$.album'),'')),
                     lower(coalesce(json_extract(item_json,'$.artist'),'')),
                     lower(coalesce(json_extract(item_json,'$.title'),'')),owner_id,device_id,media_file_id
            LIMIT @limit OFFSET @offset
            """;
        command.Parameters.AddWithValue("@query", search?.Trim() ?? "");
        command.Parameters.AddWithValue("@limit", PageSize);
        command.Parameters.AddWithValue("@offset", (long)(page - 1) * PageSize);
        var result = new List<RemoteInventoryItem>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var item = JsonSerializer.Deserialize<RemoteInventoryRecord>(reader.GetString(2))
                ?? throw new InvalidOperationException("Stored remote inventory record is invalid.");
            result.Add(new(reader.GetString(0), reader.GetString(1), item.MediaFileId, item.Path!, item.Title, item.Artist, item.Album,
                item.Label, item.Release, item.Format, item.ScannedUtc));
        }
        return new(result, total, page, PageSize);
    }
}

internal sealed class DeviceInventoryEndpoint;

internal static class RemoteInventoryEndpoints
{
    internal static void MapRemoteInventory(this WebApplication app)
    {
        app.MapPost("/api/v1/remote-inventory/batches", async (RemoteInventoryBatchRequest request,
            IRemoteInventoryService service, CancellationToken ct) =>
            TypedResults.Ok(await service.SaveAsync(request, ct)))
            .WithMetadata(new DeviceInventoryEndpoint())
            .WithName("UploadRemoteInventoryBatch").WithTags("Remote inventory")
            .WithSummary("Upsert a bounded batch of scanner metadata using the device credential")
            .Produces<RemoteInventoryBatchResponse>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(409);
        app.MapGet("/api/v1/remote-inventory", async (int? page, string? q, IRemoteInventoryService service, CancellationToken ct) =>
            TypedResults.Ok(await service.BrowseAsync(page ?? 1, q, ct)))
            .WithName("BrowseRemoteInventory").WithTags("Remote inventory")
            .WithSummary("Browse historical remote scanner records with administrator authentication")
            .Produces<RemoteInventoryPage>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(409);
        app.MapPost("/api/v1/remote-inventory/match-proposals", async (RemoteMatchProposalsRequest request,
            IRemoteInventoryService service, CancellationToken ct) => TypedResults.Ok(await service.ProposalsAsync(request, ct)))
            .WithName("ProposeRemoteInventoryMatches").WithTags("Remote inventory")
            .WithSummary("Suggest catalog recordings from historical scanner metadata")
            .Produces<IReadOnlyList<RemoteMatchProposal>>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(409);
        app.MapPost("/api/v1/remote-inventory/matches", async (RemoteMatchRequest request,
            IRemoteInventoryService service, CancellationToken ct) => TypedResults.Ok(await service.MatchAsync(request, ct)))
            .WithName("MatchRemoteInventory").WithTags("Remote inventory")
            .WithSummary("Save explicitly selected remote scanner links without reading audio")
            .Produces<RemoteMatchResult>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(409);
    }
}
