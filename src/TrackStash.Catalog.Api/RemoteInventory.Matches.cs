using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TrackStash.Catalog.Api.Middleware;
using TrackStash.Core.Storage;

namespace TrackStash.Catalog.Api;

internal sealed partial class RemoteInventoryService
{
    private static string Digest(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string RemoteMediaId(RemoteInventoryKey key) => $"remote-{Digest(JsonSerializer.Serialize(key))}";

    private static void ValidateKeys(IReadOnlyList<RemoteInventoryKey>? keys)
    {
        if (keys is null || keys.Count is < 1 or > 100 || keys.Any(key => key is null ||
            string.IsNullOrWhiteSpace(key.OwnerId) || key.OwnerId.Length > 100 ||
            string.IsNullOrWhiteSpace(key.DeviceId) || key.DeviceId.Length > 100 || key.MediaFileId <= 0) ||
            keys.Distinct().Count() != keys.Count)
            throw new ApiRequestException(400, "validation_failed", "Choose between 1 and 100 distinct remote media records.");
    }

    private async Task<(RemoteInventoryRecord Record, string Version)> ReadRemote(
        SqliteConnection connection, SqliteTransaction transaction, RemoteInventoryKey key, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT item_json FROM remote_inventory
            WHERE owner_id=@owner AND device_id=@device AND media_file_id=@id
            """;
        command.Parameters.AddWithValue("@owner", key.OwnerId);
        command.Parameters.AddWithValue("@device", key.DeviceId);
        command.Parameters.AddWithValue("@id", key.MediaFileId);
        var json = await command.ExecuteScalarAsync(ct) as string
            ?? throw new ApiRequestException(404, "remote_media_not_found", "A selected scanner record is no longer available.");
        var record = JsonSerializer.Deserialize<RemoteInventoryRecord>(json)
            ?? throw new InvalidDataException("Stored remote inventory record is invalid.");
        if (record.MediaFileId != key.MediaFileId)
            throw new InvalidDataException("Stored remote inventory identity does not match its key.");
        return (record, Digest(json));
    }

    private static async Task<string?> FingerprintKey(
        SqliteConnection connection, SqliteTransaction transaction, RemoteInventoryRecord record, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(record.FingerprintRaw) || string.IsNullOrWhiteSpace(record.FingerprintAlgorithm) ||
            string.IsNullOrWhiteSpace(record.FingerprintVersion)) return null;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT json_object('algorithm',@algorithm,'version',@version,'fingerprint',@fingerprint)";
        command.Parameters.AddWithValue("@algorithm", record.FingerprintAlgorithm);
        command.Parameters.AddWithValue("@version", record.FingerprintVersion);
        command.Parameters.AddWithValue("@fingerprint", record.FingerprintRaw);
        return MediaService.FingerprintKey((string)(await command.ExecuteScalarAsync(ct))!);
    }

    private async Task<SqliteConnection> OpenMatchStore(CancellationToken ct)
    {
        var path = DatabasePath;
        if (!File.Exists(path))
            throw new ApiRequestException(404, "remote_media_not_found", "No scanner inventory has been uploaded.");
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder {
            DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false
        }.ToString());
        try { await connection.OpenAsync(ct); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }

    public async Task<IReadOnlyList<RemoteMatchProposal>> ProposalsAsync(RemoteMatchProposalsRequest request, CancellationToken ct)
    {
        ValidateKeys(request.Items);
        await using var connection = await OpenMatchStore(ct);
        await using var transaction = connection.BeginTransaction(deferred: true);
        await CheckStore(connection, transaction, false, ct);
        await using var unit = await storage.BeginUnitOfWorkAsync(ct);
        var proposals = new List<RemoteMatchProposal>();
        foreach (var key in request.Items)
        {
            var (record, version) = await ReadRemote(connection, transaction, key, ct);
            var linked = await unit.Matches.GetBestMatchByMediaFileIdAsync(RemoteMediaId(key), ct);
            var ids = new HashSet<string>(StringComparer.Ordinal);
            if (MediaService.Isrc(record.Isrc) && await unit.Recordings.GetByIsrcAsync(record.Isrc!.Trim().ToUpperInvariant(), ct) is { } exact)
                ids.Add(exact.Id);
            var truncated = false;
            if (!string.IsNullOrWhiteSpace(record.Title))
            {
                var title = record.Title;
                var suffix = title.LastIndexOf(" (", StringComparison.Ordinal);
                if (suffix > 0 && title.EndsWith(')')) title = title[..suffix];
                var search = string.Join(" ", title.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(4));
                var page = await unit.Recordings.QueryTracksAsync(new TrackQuery(Search: search, GroupByRecording: true, PageSize: 100), ct);
                truncated = page.Total > page.Items.Count;
                foreach (var candidate in page.Items) ids.Add(candidate.RecordingId);
            }
            var candidates = new List<MediaRecordingCandidate>();
            foreach (var id in ids)
                candidates.Add(await MediaService.CompareRemote(unit, record, id, ct));
            var supported = candidates.Where(candidate => candidate.Status == "Supported").ToArray();
            proposals.Add(new(key, version, linked?.RecordingId,
                candidates.OrderBy(candidate => candidate.Blockers.Count).ThenBy(candidate => candidate.Title, StringComparer.OrdinalIgnoreCase).ToArray(),
                linked is null && !truncated && supported.Length == 1 ? supported[0].RecordingId : null));
        }
        return proposals;
    }

    public async Task<RemoteMatchResult> MatchAsync(RemoteMatchRequest request, CancellationToken ct)
    {
        if (request.Items is null || request.Items.Any(item => item is null))
            throw new ApiRequestException(400, "validation_failed", "Choose scanner records and recordings.");
        ValidateKeys(request.Items.Select(item => item.Key).ToArray());
        if (request.Items.Any(item => string.IsNullOrWhiteSpace(item.RecordingId) || item.RecordingId.Length > 200 ||
            item.Version is null || item.Version.Length != 64 || !item.Version.All(Uri.IsHexDigit)))
            throw new ApiRequestException(400, "validation_failed", "Choose existing recordings and current scanner snapshots.");
        await using var connection = await OpenMatchStore(ct);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await CheckStore(connection, transaction, false, ct);
        await using var unit = await storage.BeginUnitOfWorkAsync(ct);
        var saved = 0;
        var already = 0;
        foreach (var selection in request.Items)
        {
            var (record, version) = await ReadRemote(connection, transaction, selection.Key, ct);
            var id = RemoteMediaId(selection.Key);
            var existing = await unit.Matches.GetBestMatchByMediaFileIdAsync(id, ct);
            if (existing is not null)
            {
                if (existing.RecordingId != selection.RecordingId)
                    throw new ApiRequestException(409, "media_already_linked", "A selected scanner record is already linked to another recording.");
                already++;
                continue;
            }
            if (version != selection.Version)
                throw new ApiRequestException(409, "remote_inventory_changed", "Scanner metadata changed; reload the matching suggestions.");
            if (await unit.MediaFiles.GetByIdAsync(id, ct) is not null)
                throw new ApiRequestException(409, "media_already_registered", "A selected scanner identity is already registered without a matching link.");
            if (await unit.Recordings.GetByIdAsync(selection.RecordingId, ct) is null)
                throw new ApiRequestException(404, "recording_not_found", "A selected catalog recording no longer exists.");
            var candidate = await MediaService.CompareRemote(unit, record, selection.RecordingId, ct);
            var now = clock.GetUtcNow();
            var reviewId = Guid.NewGuid().ToString("N");
            var snapshot = record with { FingerprintRaw = null };
            var fingerprint = await FingerprintKey(connection, transaction, record, ct);
            var media = new MediaInventoryItem(record.MediaFileId, selection.Key.DeviceId, record.Path!,
                record.Title, record.Subtitle, record.Artist, record.Album, record.Isrc, record.DurationSeconds,
                null, record.SizeBytes, record.ScannedUtc.ToString("O"), record.LastModifiedUtc.ToString("O"),
                record.Path!, record.Label, record.Release, fingerprint);
            var evidence = JsonSerializer.Serialize(new { Type = "RemoteMediaLink", Key = selection.Key, Version = version,
                ScannerRecord = snapshot, Candidate = candidate, FileVerified = false });
            await unit.MediaFiles.UpsertAsync(new MediaFile { Id = id, Path = record.Path!,
                MetadataJson = JsonSerializer.Serialize(media), SourcePayloadJson = evidence,
                CreatedUtc = now, UpdatedUtc = now }, ct);
            await unit.Matches.UpsertBestMatchAsync(new MatchRecord { MediaFileId = id, RecordingId = selection.RecordingId,
                OverrideState = MatchOverrideState.Manual, Score = 0, Confidence = 0, EvidenceJson = evidence,
                CreatedUtc = now, UpdatedUtc = now }, ct);
            await unit.Enrichment!.SaveReviewAsync(new(reviewId, CatalogEntityKind.Recording, selection.RecordingId, version, evidence, now), ct);
            await unit.Enrichment.SaveAuditAsync(new(reviewId, "[\"RemoteMediaLink\"]",
                "User selected the catalog recording using historical scanner metadata; file bytes were not verified.",
                evidence, JsonSerializer.Serialize(new { MediaId = id, selection.RecordingId }), now), ct);
            saved++;
        }
        await unit.CommitAsync(ct);
        await transaction.CommitAsync(ct);
        return new(saved, already);
    }
}
