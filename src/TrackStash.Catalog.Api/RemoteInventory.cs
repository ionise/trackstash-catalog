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
    string? Artist, string? Album, string? Label, string? Release, string? Format, DateTimeOffset ScannedUtc, string Version,
    string? Mix = null, string? Isrc = null, double? DurationSeconds = null, long SizeBytes = 0,
    DateTimeOffset? LastModifiedUtc = null, MediaFileTags? Tags = null);
/// <summary>A page of remote inventory records.</summary>
public sealed record RemoteInventoryPage(IReadOnlyList<RemoteInventoryItem> Items, int Total, int Page, int PageSize, IReadOnlyList<string> Devices);
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
/// <summary>One remote scanner snapshot and its explicit pseudo-track choice.</summary>
public sealed record RemotePseudoChoice(RemoteInventoryKey Key, string Version, string? RecordingId, long? CopyOfIndex,
    string? Title, string? Mix, string? ArtistId, string? NewArtist);
/// <summary>Creates a pseudo release from uploaded scanner snapshots without reading device files.</summary>
public sealed record CreateRemotePseudoRequest(string OperationId, string Title, string? CatalogNumber,
    IReadOnlyList<RemotePseudoChoice> Items);

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
    Task<(RemoteInventoryRecord Record, string Version)> LookupItemAsync(RemoteInventoryKey key, CancellationToken ct);
    Task<RemoteInventoryBatchResponse> SaveAsync(RemoteInventoryBatchRequest batch, CancellationToken ct);
    Task<RemoteInventoryPage> BrowseAsync(int page, string? search, string? device, string? match, string? sort, bool descending, CancellationToken ct);
    Task<IReadOnlyList<RemoteMatchProposal>> ProposalsAsync(RemoteMatchProposalsRequest request, CancellationToken ct);
    Task<RemoteMatchResult> MatchAsync(RemoteMatchRequest request, CancellationToken ct);
    Task<PseudoReleaseOutcome> CreatePseudoAsync(CreateRemotePseudoRequest request, CancellationToken ct);
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

    public async Task<RemoteInventoryPage> BrowseAsync(int page, string? search, string? device, string? match, string? sort, bool descending, CancellationToken ct)
    {
        if (page < 1 || search?.Length > 200 || device?.Length > 100 || device is not null && string.IsNullOrWhiteSpace(device) ||
            match is not (null or "matched" or "unmatched") || sort is not (null or "release" or "title" or "artist" or "label" or "device"))
            throw new ApiRequestException(400, "validation_failed", "Invalid inventory page, search, device, match status or sort.");
        var path = DatabasePath;
        if (!File.Exists(path)) return new([], 0, page, PageSize, []);
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        await connection.OpenAsync(ct);
        if (match is not null)
        {
            await using var unit = await storage.BeginUnitOfWorkAsync(ct);
            var linked = await unit.Matches.ListMatchedMediaIdsAsync("remote-", ct);
            await using var temp = connection.CreateCommand();
            temp.CommandText = "CREATE TEMP TABLE linked_remote(media_id TEXT PRIMARY KEY) WITHOUT ROWID";
            await temp.ExecuteNonQueryAsync(ct);
            temp.CommandText = "INSERT INTO linked_remote(media_id) VALUES(@id)";
            var parameter = temp.Parameters.Add("@id", SqliteType.Text);
            foreach (var id in linked)
            {
                parameter.Value = id;
                await temp.ExecuteNonQueryAsync(ct);
            }
            connection.CreateFunction<string, string, long, string>("remote_media_id",
                (owner, deviceId, mediaId) => RemoteMediaId(new(owner, deviceId, mediaId)), isDeterministic: true);
        }
        const string filter = """
            (@query = '' OR instr(lower(coalesce(json_extract(item_json,'$.title'),'')),lower(@query)) > 0
             OR instr(lower(coalesce(json_extract(item_json,'$.artist'),'')),lower(@query)) > 0
             OR instr(lower(coalesce(json_extract(item_json,'$.label'),'')),lower(@query)) > 0
             OR instr(lower(coalesce(json_extract(item_json,'$.release'),'')),lower(@query)) > 0
             OR instr(lower(coalesce(json_extract(item_json,'$.album'),'')),lower(@query)) > 0)
            AND (@device IS NULL OR device_id=@device)
            """;
        await using var transaction = connection.BeginTransaction(deferred: true);
        await CheckStore(connection, transaction, false, ct);
        await using var devicesCommand = connection.CreateCommand();
        devicesCommand.Transaction = transaction;
        devicesCommand.CommandText = "SELECT DISTINCT device_id FROM remote_inventory ORDER BY device_id";
        var devices = new List<string>();
        await using (var devicesReader = await devicesCommand.ExecuteReaderAsync(ct))
            while (await devicesReader.ReadAsync(ct)) devices.Add(devicesReader.GetString(0));
        var matchFilter = match is null ? "" : match == "matched"
            ? " AND EXISTS(SELECT 1 FROM linked_remote WHERE media_id=remote_media_id(owner_id,device_id,media_file_id))"
            : " AND NOT EXISTS(SELECT 1 FROM linked_remote WHERE media_id=remote_media_id(owner_id,device_id,media_file_id))";
        await using var count = connection.CreateCommand();
        count.Transaction = (SqliteTransaction)transaction;
        count.CommandText = $"SELECT count(*) FROM remote_inventory WHERE {filter} {matchFilter}";
        count.Parameters.AddWithValue("@query", search?.Trim() ?? "");
        count.Parameters.AddWithValue("@device", device ?? (object)DBNull.Value);
        var total = Convert.ToInt32(await count.ExecuteScalarAsync(ct));
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        var sortExpression = sort switch
        {
            "title" => "lower(coalesce(json_extract(item_json,'$.title'),''))",
            "artist" => "lower(coalesce(json_extract(item_json,'$.artist'),''))",
            "label" => "lower(coalesce(json_extract(item_json,'$.label'),''))",
            "device" => "lower(device_id)",
            _ => "lower(coalesce(json_extract(item_json,'$.release'),json_extract(item_json,'$.album'),''))",
        };
        var direction = descending ? "DESC" : "ASC";
        command.CommandText = $"""
            SELECT owner_id,device_id,item_json FROM remote_inventory WHERE {filter} {matchFilter}
            ORDER BY {sortExpression} {direction},
                     lower(coalesce(json_extract(item_json,'$.artist'),'')),
                     lower(coalesce(json_extract(item_json,'$.title'),'')),owner_id,device_id,media_file_id
            LIMIT @limit OFFSET @offset
            """;
        command.Parameters.AddWithValue("@query", search?.Trim() ?? "");
        command.Parameters.AddWithValue("@device", device ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@limit", PageSize);
        command.Parameters.AddWithValue("@offset", (long)(page - 1) * PageSize);
        var result = new List<RemoteInventoryItem>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var item = JsonSerializer.Deserialize<RemoteInventoryRecord>(reader.GetString(2))
                ?? throw new InvalidOperationException("Stored remote inventory record is invalid.");
            result.Add(new(reader.GetString(0), reader.GetString(1), item.MediaFileId, item.Path!, item.Title, item.Artist, item.Album,
                item.Label, item.Release, item.Format, item.ScannedUtc, Digest(reader.GetString(2)),
                item.Subtitle, item.Isrc, item.DurationSeconds, item.SizeBytes, item.LastModifiedUtc,
                new(item.ArtistsJson, item.RemixersJson, item.Barcode, item.CatalogNumber,
                    TagValue(item.TrackNumber), TagValue(item.DiscNumber), TagValue(item.Bpm), item.MusicalKey, item.Genre,
                    item.Year?.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        }
        return new(result, total, page, PageSize, devices);
    }

    private static string? TagValue(JsonElement? value) => value is not { } element || element.ValueKind == JsonValueKind.Null
        ? null : element.ValueKind == JsonValueKind.String ? element.GetString() : element.GetRawText();
}

internal sealed class DeviceInventoryEndpoint;

internal static class RemoteInventoryEndpoints
{
    internal static void MapRemoteInventory(this WebApplication app)
    {
        app.MapPost("/api/v1/remote-inventory/pseudo-releases", async (CreateRemotePseudoRequest request,
            IRemoteInventoryService service, CancellationToken ct) =>
        {
            var result = await service.CreatePseudoAsync(request, ct);
            return TypedResults.Created($"/api/v1/releases/{result.ReleaseId}", result);
        }).WithName("CreateRemotePseudoRelease").WithTags("Remote inventory")
          .WithSummary("Create a pseudo release and link selected uploaded device records")
          .WithDescription("Snapshot versions and Catalog identities are checked atomically. Device file bytes are not verified; identical retries return the saved outcome.")
          .Produces<PseudoReleaseOutcome>(201).ProducesProblem(400).ProducesProblem(409).ProducesProblem(412);
        app.MapPost("/api/v1/remote-inventory/batches", async (RemoteInventoryBatchRequest request,
            IRemoteInventoryService service, CancellationToken ct) =>
            TypedResults.Ok(await service.SaveAsync(request, ct)))
            .WithMetadata(new DeviceInventoryEndpoint())
            .WithName("UploadRemoteInventoryBatch").WithTags("Remote inventory")
            .WithSummary("Upsert a bounded batch of scanner metadata using the device credential")
            .Produces<RemoteInventoryBatchResponse>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(409);
        app.MapGet("/api/v1/remote-inventory", async (int? page, string? q, string? device, string? match, string? sort, string? order, IRemoteInventoryService service, CancellationToken ct) =>
        {
            if (order is not (null or "asc" or "desc"))
                throw new ApiRequestException(400, "validation_failed", "Inventory order must be asc or desc.");
            return TypedResults.Ok(await service.BrowseAsync(page ?? 1, q, device, match, sort, order == "desc", ct));
        })
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
