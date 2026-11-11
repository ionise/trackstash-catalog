using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using TrackStash.Catalog.Application;
using TrackStash.Core.Normalization;
using TrackStash.Core.Storage;

namespace TrackStash.Catalog.Api;

/// <summary>Named server-approved library, not a browser-supplied filesystem root.</summary>
public sealed record MediaLibrary(string Id, string Root, string Name);
/// <summary>Historical scanner tags and file identity; existence does not imply verified bytes or backup completeness.</summary>
public sealed record MediaInventoryItem(long Id, string Library, string RelativePath, string? Title, string? Mix,
    string? Artist, string? Album, string? Isrc, double? DurationSeconds, string? ContentHash, long SizeBytes,
    string ScannedUtc, string LastModifiedUtc, string Path, string? Label = null, string? Release = null, string? FingerprintKey = null);
/// <summary>A bounded scanner inventory page, with explicit freshness/ownership limitations.</summary>
public sealed record MediaInventoryPage(IReadOnlyList<MediaInventoryItem> Items, int Total, int Page, int PageSize,
    IReadOnlyList<string> Libraries, IReadOnlyList<string> Warnings, IReadOnlyList<MediaGroup>? Groups = null, int? GroupTotal = null,
    IReadOnlyDictionary<string, string>? LibraryNames = null);
/// <summary>Tagged Recording candidate, reasons and blockers; confidence is not automatic acceptance.</summary>
public sealed record MediaRecordingCandidate(string RecordingId, string? Title, string? Mix, string Status,
    IReadOnlyList<string> Evidence, IReadOnlyList<string> Blockers);
/// <summary>Candidate matches for one scanner-scoped media file.</summary>
public sealed record MediaCandidates(MediaInventoryItem Media, IReadOnlyList<MediaRecordingCandidate> Candidates, string? LinkedRecordingId);
/// <summary>Frozen verified file and Recording evidence for explicit acceptance.</summary>
public sealed record MediaLinkReview(string Id, string Version, MediaInventoryItem Media, MediaRecordingCandidate Candidate,
    DateTimeOffset CreatedAt, string VerifiedSha256, string? SidecarTitle, string? SidecarMix, MediaLinkOutcome? Outcome = null);
/// <summary>Saved media association and immutable audit identity; no purchase assertion.</summary>
public sealed record MediaLinkOutcome(string ReviewId, string MediaId, string RecordingId, DateTimeOffset AppliedAt);
/// <summary>Persisted association, not a fresh file-presence or backup verification result.</summary>
public sealed record LinkedRecordingMedia(string Id, string Path, string? Sha256, DateTimeOffset? LinkedAt,
    string? Library = null, string? LibraryName = null, string? Format = null, string? FingerprintKey = null);

public interface IMediaService
{
    Task<MediaInventoryPage> QueryAsync(string? library, string? search, int page, CancellationToken ct, MediaBrowseOptions? options = null);
    Task<MediaCandidates> CandidatesAsync(long id, CancellationToken ct);
    Task<MediaLinkReview> PrepareAsync(long id, string recordingId, CancellationToken ct);
    Task<MediaLinkReview> GetReviewAsync(string id, CancellationToken ct);
    Task<MediaLinkOutcome> ApplyAsync(string id, string version, string reason, bool confirmed, CancellationToken ct);
    Task<IReadOnlyList<LinkedRecordingMedia>> LinkedAsync(string recordingId, CancellationToken ct);
    Task<MediaReleaseReview> PrepareReleaseAsync(string groupKey, string releaseId, CancellationToken ct);
    Task<MediaReleaseReview> GetReleaseReviewAsync(string id, CancellationToken ct);
    Task<MediaReleaseAssociation> ApplyReleaseAsync(string id, string version, string reason, bool confirmed, CancellationToken ct);
    Task<IReadOnlyList<MediaReleaseAssociation>> ReleaseAssociationsAsync(string releaseId, CancellationToken ct);
    Task<MediaReconciliationPlan> ReconcileAsync(string groupKey, string releaseId, CancellationToken ct);
    Task<MediaReconciliationReview> PrepareReconciliationAsync(string groupKey, string releaseId, string version, IReadOnlyList<MediaTrackSelection> selections, CancellationToken ct);
    Task<MediaReconciliationReview> GetReconciliationAsync(string id, CancellationToken ct);
    Task<MediaReconciliationOutcome> ApplyReconciliationAsync(string id, string version, string reason, bool confirmed, CancellationToken ct);
    Task<MediaMatchPlan> MatchPlanAsync(IReadOnlyList<long> mediaIds, CancellationToken ct);
    Task<MediaMatchReview> PrepareMatchesAsync(IReadOnlyList<long> mediaIds, IReadOnlyList<MediaMatchSelection> selections, string version, CancellationToken ct);
    Task<MediaMatchReview> GetMatchesAsync(string id, CancellationToken ct);
    Task<MediaMatchOutcome> ApplyMatchesAsync(string id, string version, bool confirmed, CancellationToken ct);
}

public sealed partial class MediaService(IConfiguration configuration, IStorageProvider storage, TimeProvider clock,
    IMediaLibrarySettings librarySettings) : IMediaService
{
    private sealed record RecordingState(Recording Recording, IReadOnlyList<CatalogCredit> Credits,
        IReadOnlyList<string> PrimaryArtists);
    private sealed record LinkPayload(string Type, string SourceId, MediaLinkReview Review, RecordingState Recording,
        string? SidecarHash);
    private sealed record Sidecar(string? Title, string? Mix, string? Isrc, string? TrackId, string? Provider, string Hash);
    private static EnrichmentApplyException Error(int status, string code, string message) => new(status, code, message);
    private static string Json<T>(T value) => JsonSerializer.Serialize(value);
    private static string Hash<T>(T value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Json(value)))).ToLowerInvariant();
    public async Task<IReadOnlyList<LinkedRecordingMedia>> LinkedAsync(string recordingId, CancellationToken ct)
    {
        await using var unit = await storage.BeginUnitOfWorkAsync(ct);
        if (await unit.Recordings.GetByIdAsync(recordingId, ct) is null) throw Error(404, "recording_not_found", "Recording was not found.");
        var items = await unit.MediaFiles.ListByRecordingAsync(recordingId, ct);
        if (items.Count > 100) throw Error(409, "media_list_limit", "More than 100 linked copies; a paged media view is required.");
        if (items.Count == 0) return [];
        var settings = librarySettings.Read().Libraries;
        return items.Select(f => {
            MediaInventoryItem? snapshot = null;
            if (f.Id.StartsWith("scanner-", StringComparison.Ordinal) && f.MetadataJson is not null)
            {
                try { snapshot = JsonSerializer.Deserialize<MediaInventoryItem>(f.MetadataJson); }
                catch (JsonException) { throw Error(409, "media_evidence_invalid", "Stored linked-copy metadata is malformed; its audio evidence cannot be displayed."); }
                if (snapshot is null || snapshot.Path != f.Path)
                    throw Error(409, "media_evidence_invalid", "Stored linked-copy metadata does not identify its registered path.");
            }
            var library = snapshot is not null ? settings.FirstOrDefault(l => l.Id == snapshot.Library) :
                settings.OrderByDescending(l => l.Root.Length).FirstOrDefault(l => Within(f.Path, l.Root));
            return new LinkedRecordingMedia(f.Id, f.Path, f.ContentHash, f.CreatedUtc, snapshot?.Library ?? library?.Id,
                library?.Name ?? snapshot?.Library, System.IO.Path.GetExtension(f.Path).TrimStart('.').ToUpperInvariant(),
                snapshot?.FingerprintKey);
        }).ToArray();
    }
    private string Scanner => configuration["CatalogApi:ScannerDatabasePath"] ??
        Environment.GetEnvironmentVariable("TRACKSTASH_SCAN_DATABASE_PATH") ??
        throw Error(409, "media_not_configured", "Configure ScannerDatabasePath and named MediaLibraries on the server.");
    private string SourceId => Hash(System.IO.Path.GetFullPath(Scanner));
    private string MediaId(long id) => $"scanner-{SourceId}-{id}";
    private IReadOnlyList<MediaLibrary>? libraries;
    private IReadOnlyList<MediaLibrary> Libraries => libraries ??= librarySettings.Read().Libraries.Where(l => l.Enabled)
        .Select(l => new MediaLibrary(l.Id, l.Root, l.Name)).ToArray();
    private static bool Within(string path, string root) => path.StartsWith(root.TrimEnd(System.IO.Path.DirectorySeparatorChar) +
        System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal);
    private MediaLibrary Library(string path) => Libraries.OrderByDescending(l => l.Root.Length).FirstOrDefault(l => Within(path, l.Root))
        ?? throw Error(409, "media_outside_library", "The scanner file is outside approved media libraries.");
    private async Task<SqliteConnection> Open(CancellationToken ct)
    {
        if (Libraries.Count == 0 || !File.Exists(Scanner))
            throw Error(409, "media_not_configured", "Configure an existing ScannerDatabasePath and named MediaLibraries; inventory does not rescan disks.");
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = System.IO.Path.GetFullPath(Scanner), Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        try { await connection.OpenAsync(ct); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }
    private async Task<List<MediaInventoryItem>> Inventory(CancellationToken ct, long? id = null)
    {
        try { return await ReadInventory(ct, id); }
        catch (SqliteException) { throw Error(409, "media_inventory_unavailable", "Scanner database could not be read or has an incompatible media/metadata schema. Check its configuration and scanner version."); }
        catch (InvalidCastException) { throw Error(409, "media_inventory_invalid", "Scanner metadata has invalid field types; refresh or repair its inventory before linking."); }
    }
    private async Task<List<MediaInventoryItem>> ReadInventory(CancellationToken ct, long? id)
    {
        await using var connection = await Open(ct);
        var metadataColumns = await Columns(connection, "metadata", ct);
        var fileColumns = await Columns(connection, "media_file", ct);
        var labelColumn = metadataColumns.Contains("label") ? "m.label" : "NULL";
        var releaseColumn = metadataColumns.Contains("release") ? "nullif(trim(m.release),'')" : "NULL";
        var fingerprintColumn = fileColumns.Contains("fingerprint_raw")
            ? "CASE WHEN length(f.fingerprint_raw)>0 THEN json_object('algorithm','legacy-chromaprint','version','unknown','fingerprint',f.fingerprint_raw) END" : "NULL";
        var fingerprintColumns = await Columns(connection, "acoustic_fingerprint", ct);
        if (fingerprintColumns.Count > 0)
            fingerprintColumn = $"""
                coalesce((SELECT json_object('algorithm',a.algorithm,'version',a.algorithm_version,'fingerprint',a.fingerprint_raw)
                  FROM acoustic_fingerprint a WHERE a.media_file_id=f.media_file_id
                  AND a.generation_status='complete' AND length(a.fingerprint_raw)>0
                  ORDER BY a.generated_utc DESC,a.algorithm,a.algorithm_version LIMIT 1),{fingerprintColumn})
                """;
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT f.media_file_id,f.path,m.title,m.subtitle,m.artist,m.album,m.isrc,f.duration_seconds,
                   f.content_hash,f.size_bytes,f.scanned_utc,f.last_modified_utc,{labelColumn},{releaseColumn},{fingerprintColumn}
            FROM media_file f LEFT JOIN metadata m USING(media_file_id)
            WHERE @id IS NULL OR f.media_file_id=@id ORDER BY f.media_file_id LIMIT 100001
            """;
        command.Parameters.AddWithValue("@id", (object?)id ?? DBNull.Value);
        var rows = new List<MediaInventoryItem>();
        var inventoryCount = 0;
        var libraries = Libraries.OrderByDescending(l => l.Root.Length).ToArray();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (++inventoryCount > 100000) throw Error(409, "media_inventory_limit", "This first media workflow supports at most 100000 inventory rows; use a partitioned inventory.");
            var path = reader.GetString(1);
            var library = libraries.FirstOrDefault(l => Within(path, l.Root));
            if (library is null) continue;
            string? Text(int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
            rows.Add(new(reader.GetInt64(0), library.Id, System.IO.Path.GetRelativePath(library.Root, path),
                Text(2), Text(3), Text(4), Text(5), Text(6), reader.IsDBNull(7) ? null : reader.GetDouble(7),
                Text(8), reader.GetInt64(9), reader.GetString(10), reader.GetString(11), path, Text(12),
                Text(13) ?? Text(5), string.IsNullOrWhiteSpace(Text(14)) ? null : Hash(Text(14))));
        }
        return rows;
    }
    public async Task<MediaInventoryPage> QueryAsync(string? library, string? search, int page, CancellationToken ct, MediaBrowseOptions? options = null)
    {
        if (page < 1 || search?.Length > 200 || library is not null && !Libraries.Any(l => l.Id == library))
            throw Error(400, "validation_failed", "Choose a configured library, page >=1 and search <=200 characters.");
        var rows = await Inventory(ct);
        if (options?.Classification is { } classification)
        {
            if (classification is not ("unofficial" or "unclassified"))
                throw Error(400, "validation_failed", "Choose an independent media classification.");
            await Ready(ct);
            await using var unit = await storage.BeginUnitOfWorkAsync(ct);
            var classifications = await MediaClassifications(unit, ct);
            rows = rows.Where(r => classifications.GetValueOrDefault(r.Id, "unclassified") == classification).ToList();
        }
        var browse = Browse(rows, library, search, page, options ?? new());
        if (browse.Groups.Length > 0 && await storage.Migrations.GetCurrentVersionAsync(ct) >= 13)
        {
            await using var unit = await storage.BeginUnitOfWorkAsync(ct);
            var applied = await AppliedReleaseGroups(unit, ct);
            browse.Groups = browse.Groups.Select(g => {
                var accepted = applied.FirstOrDefault(p => p.SourceId == SourceId && p.Review.GroupKey == g.Key);
                return g with { AssociatedReleaseId = accepted?.Review.ReleaseId, AssociatedReleaseTitle = accepted?.Review.CatalogTitle };
            }).ToArray();
        }
        return new(browse.Items, browse.Total, page, options?.PageSize ?? 25,
            Libraries.Select(l => l.Id).ToArray(), [
                "Historical scanner inventory, not a fresh filesystem scan. Missing or moved files require scanner refresh; nothing is inferred from absence.",
                "Tags, duration and sidecars propose identity. Listing does not read audio; preparing a review verifies SHA-256 and may download a cloud-only Dropbox file.",
                "Library locations are copies, not independent Recordings or acquisition evidence. NAS presence is not proof of complete backup coverage."
            ], browse.Groups, browse.GroupTotal, Libraries.ToDictionary(l => l.Id, l => l.Name, StringComparer.Ordinal));
    }
    private async Task<MediaInventoryItem> Item(long id, CancellationToken ct) =>
        (await Inventory(ct, id)).SingleOrDefault(r => r.Id == id)
        ?? throw Error(404, "media_not_found", "Scanner media file was not found in approved libraries.");
    private static bool Same(string? a, string? b) => !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) &&
        EntityNameNormalizer.NormalizeStrict(a) == EntityNameNormalizer.NormalizeStrict(b);
    private static bool Isrc(string? value) => value is not null && Regex.IsMatch(value.Trim().ToUpperInvariant(), "^[A-Z]{2}[A-Z0-9]{3}[0-9]{7}$");
    private static async Task<RecordingState> ReadRecording(IUnitOfWork unit, string id, CancellationToken ct)
    {
        var recording = await unit.Recordings.GetByIdAsync(id, ct) ?? throw Error(404, "recording_not_found", "Recording was not found.");
        var credits = await unit.CatalogFoundations!.GetCreditsAsync(CatalogEntityKind.Recording, id, ct);
        var artists = new List<string>();
        foreach (var credit in credits.Where(c => c.Role == "primary"))
        {
            var artist = await unit.Artists.GetByIdAsync(credit.ArtistId, ct);
            var name = credit.CreditedName ?? artist?.Name;
            if (!string.IsNullOrWhiteSpace(name)) artists.Add(name);
        }
        return new(recording, credits, artists);
    }
    private static MediaRecordingCandidate Compare(MediaInventoryItem media, RecordingState state, Sidecar? sidecar = null)
    {
        var r = state.Recording;
        var evidence = new List<string>();
        var blockers = new List<string>();
        var isrcMatch = Isrc(media.Isrc) && string.Equals(media.Isrc!.Trim(), r.Isrc?.Trim(), StringComparison.OrdinalIgnoreCase);
        if (isrcMatch) evidence.Add("Valid tagged ISRC agrees.");
        if (Isrc(media.Isrc) && Isrc(r.Isrc) && !isrcMatch) blockers.Add("Tagged ISRC conflicts.");
        var title = r.Title ?? r.Name;
        var combined = !string.IsNullOrWhiteSpace(r.MixName) && Same(media.Title, $"{title} ({r.MixName})");
        var titleMatch = Same(media.Title, title) || combined;
        if (titleMatch) evidence.Add(combined ? "Tagged title explicitly includes the catalog mix/version." : "Tagged title agrees after punctuation/case normalization.");
        else if (!string.IsNullOrWhiteSpace(media.Title)) blockers.Add("Tagged title conflicts.");
        var mixMatch = Same(media.Mix, r.MixName) || combined || Same(sidecar?.Mix, r.MixName);
        if (!string.IsNullOrWhiteSpace(media.Mix) && !string.IsNullOrWhiteSpace(r.MixName) && !Same(media.Mix, r.MixName))
            blockers.Add("Tagged mix/version conflicts.");
        if (mixMatch) evidence.Add("Explicit mix/version evidence agrees.");
        else evidence.Add("Mix/version evidence is missing; Original Mix is not inferred.");
        var artistMatch = state.PrimaryArtists.Count > 0 && (Same(media.Artist, string.Join(" & ", state.PrimaryArtists)) ||
            state.PrimaryArtists.Count == 1 && Same(media.Artist, state.PrimaryArtists[0]));
        if (artistMatch) evidence.Add("Primary Artist tag agrees.");
        else if (!string.IsNullOrWhiteSpace(media.Artist) && state.PrimaryArtists.Count > 0) blockers.Add("Primary Artist tag conflicts; review spelling/compound credits before linking.");
        var durationMatch = false;
        if (media.DurationSeconds is { } seconds && r.DurationMs is { } ms)
        {
            var delta = Math.Abs(seconds * 1000 - ms);
            durationMatch = double.IsFinite(delta) && delta <= 2000;
            if (durationMatch) evidence.Add($"Duration agrees within 2 seconds ({delta:F0} ms difference).");
            else blockers.Add($"Duration differs by {delta:F0} ms; investigate version/audio before linking.");
        }
        if (sidecar is not null)
        {
            evidence.Add("A scoped release sidecar describes this scanner file; it is not a canonical Catalog identity.");
            if (sidecar.Title is not null && !Same(sidecar.Title, title)) blockers.Add("Sidecar title conflicts.");
            if (sidecar.Mix is not null && r.MixName is not null && !Same(sidecar.Mix, r.MixName)) blockers.Add("Sidecar mix/version conflicts.");
            if (Isrc(sidecar.Isrc) && Isrc(r.Isrc) && !string.Equals(sidecar.Isrc, r.Isrc, StringComparison.OrdinalIgnoreCase)) blockers.Add("Sidecar ISRC conflicts.");
            if (sidecar.Provider is { } provider && sidecar.TrackId is { } trackId &&
                r.ExternalReferences.Any(reference => reference.Source.Equals(provider, StringComparison.OrdinalIgnoreCase) && reference.ExternalId != trackId))
                blockers.Add("Sidecar provider track identity conflicts.");
        }
        var eligible = blockers.Count == 0 && (isrcMatch || titleMatch && mixMatch && artistMatch && durationMatch);
        return new(r.Id, title, r.MixName, !eligible ? "Blocked" : titleMatch && mixMatch && artistMatch && durationMatch ? "Supported" : "Needs manual version review",
            evidence, blockers.Count > 0 ? blockers : !eligible ? ["Insufficient independent tagged metadata to accept a link."] : []);
    }
    public async Task<MediaCandidates> CandidatesAsync(long id, CancellationToken ct)
    {
        var media = await Item(id, ct);
        await using var unit = await storage.BeginUnitOfWorkAsync(ct);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (Isrc(media.Isrc) && await unit.Recordings.GetByIsrcAsync(media.Isrc!.Trim().ToUpperInvariant(), ct) is { } exact) ids.Add(exact.Id);
        if (!string.IsNullOrWhiteSpace(media.Title))
        {
            var suffix = media.Title.LastIndexOf(" (", StringComparison.Ordinal);
            var retrievalTitle = suffix > 0 && media.Title.EndsWith(')') ? media.Title[..suffix] : media.Title;
            var words = retrievalTitle.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(4);
            var page = await unit.Recordings.QueryTracksAsync(new TrackQuery(Search: string.Join(" ", words), GroupByRecording: true, PageSize: 25), ct);
            foreach (var row in page.Items) ids.Add(row.RecordingId);
        }
        var candidates = new List<MediaRecordingCandidate>();
        foreach (var recordingId in ids) candidates.Add(Compare(media, await ReadRecording(unit, recordingId, ct)));
        var linked = await unit.Matches.GetBestMatchByMediaFileIdAsync(MediaId(id), ct);
        return new(media, candidates.OrderBy(c => c.Blockers.Count).ThenBy(c => c.RecordingId, StringComparer.Ordinal).ToArray(), linked?.RecordingId);
    }
    private async Task Ready(CancellationToken ct)
    {
        if (await storage.Migrations.GetCurrentVersionAsync(ct) < 13)
            throw Error(409, "enrichment_schema_required", "Audited media linking requires schema 13; no automatic migration is performed.");
    }
    private string SafePath(MediaInventoryItem media)
    {
        var library = Library(media.Path);
        var root = new DirectoryInfo(library.Root);
        if (!root.Exists) throw Error(409, "media_unavailable", "The configured library is not mounted/available.");
        if (root.LinkTarget is not null) throw Error(409, "media_path_unsafe", "Library roots cannot be symlinks in this workflow.");
        for (var parent = root.Parent; parent is not null; parent = parent.Parent)
            if (parent.LinkTarget is not null) throw Error(409, "media_path_unsafe", "Library root ancestors cannot be symlinks in this workflow.");
        var cursor = library.Root;
        foreach (var part in System.IO.Path.GetRelativePath(library.Root, media.Path).Split(System.IO.Path.DirectorySeparatorChar))
        {
            if (part is ".." or ".") throw Error(409, "media_path_unsafe", "Media path is outside the approved library.");
            cursor = System.IO.Path.Combine(cursor, part);
            FileSystemInfo info = Directory.Exists(cursor) ? new DirectoryInfo(cursor) : new FileInfo(cursor);
            if (info.LinkTarget is not null) throw Error(409, "media_path_unsafe", "Symlinked media paths require separate review.");
        }
        return cursor;
    }
    private async Task<(string Hash, DateTime Modified)> Verify(MediaInventoryItem media, CancellationToken ct, string? expectedSha256 = null)
    {
        var scannerHash = string.IsNullOrWhiteSpace(media.ContentHash) ? null : media.ContentHash;
        if (scannerHash is not null && !Regex.IsMatch(scannerHash, "^[0-9a-fA-F]{64}$"))
            throw Error(409, "media_hash_invalid", $"Media {media.Id} has an invalid scanner SHA-256; refresh that file's scanner record before linking.");
        if (expectedSha256 is not null && !Regex.IsMatch(expectedSha256, "^[0-9a-fA-F]{64}$"))
            throw Error(409, "media_review_invalid", "The frozen review has no valid verified SHA-256; prepare a fresh review.");
        var expectedHash = expectedSha256 ?? scannerHash;
        var path = SafePath(media);
        var before = new FileInfo(path);
        if (!before.Exists || before.Length != media.SizeBytes)
            throw Error(409, "media_unavailable", "Media is missing or size changed since scanning. Refresh the inventory.");
        var modified = before.LastWriteTimeUtc;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, deadline.Token)).ToLowerInvariant();
            before.Refresh();
            if (!before.Exists || before.Length != media.SizeBytes || before.LastWriteTimeUtc != modified ||
                expectedHash is not null && !hash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                throw Error(409, "media_changed", expectedSha256 is not null
                    ? $"Media {media.Id} bytes changed since verification; prepare a fresh review."
                    : $"Media {media.Id} bytes differ from its scanner hash, possibly after tag edits; refresh that file's scanner record before linking.");
            return (hash, modified);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw Error(504, "media_verify_timeout", "Media verification timed out; no link was saved."); }
        catch (IOException) { throw Error(409, "media_unavailable", "Media could not be read; no link was saved."); }
        catch (UnauthorizedAccessException) { throw Error(409, "media_unavailable", "Media read access was denied; no link was saved."); }
    }
    private void CheckVerifiedFile(MediaInventoryItem media, DateTime modified)
    {
        var file = new FileInfo(SafePath(media));
        if (!file.Exists || file.Length != media.SizeBytes || file.LastWriteTimeUtc != modified)
            throw Error(409, "media_changed", "Media changed after byte verification; nothing was saved. Prepare a fresh review.");
    }
    private async Task<Sidecar?> ReadSidecar(MediaInventoryItem media, CancellationToken ct)
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(SafePath(media))!, ".trackstash-release.json");
        var file = new FileInfo(path);
        if (!file.Exists) return null;
        if (file.LinkTarget is not null || file.Length > 1024 * 1024)
            throw Error(409, "media_sidecar_invalid", "Sidecar is symlinked or oversized; no identity was inferred.");
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var length = stream.Length;
            var modified = file.LastWriteTimeUtc;
            if (length > 1024 * 1024) throw Error(409, "media_sidecar_invalid", "Sidecar exceeds the 1 MiB limit.");
            var bytes = new byte[(int)length];
            await stream.ReadExactlyAsync(bytes, ct);
            file.Refresh();
            if (stream.Length != length || !file.Exists || file.Length != length || file.LastWriteTimeUtc != modified)
                throw Error(409, "media_sidecar_invalid", "Sidecar changed while being read; prepare a fresh review.");
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (root.GetProperty("schemaVersion").GetInt32() != 4 || root.GetProperty("artifactType").GetString() != "trackstash.release-directory")
                throw Error(409, "media_sidecar_invalid", "Unsupported release sidecar; refresh/review it before linking.");
            var matches = root.GetProperty("tracks").EnumerateArray().SelectMany(t => t.GetProperty("localFiles").EnumerateArray()
                .Where(f => f.GetProperty("mediaFileId").GetInt64() == media.Id &&
                    f.GetProperty("currentRelativePath").ValueKind == JsonValueKind.String &&
                    media.Path.EndsWith("/" + f.GetProperty("currentRelativePath").GetString(), StringComparison.Ordinal)).Select(_ => t)).ToArray();
            if (matches.Length != 1) throw Error(409, "media_sidecar_invalid", "Sidecar does not uniquely identify this scanner file at its current path.");
            static string? Text(JsonElement value, string key)
            {
                if (!value.TryGetProperty(key, out var item) || item.ValueKind == JsonValueKind.Null) return null;
                if (item.ValueKind != JsonValueKind.String) throw new JsonException("Expected sidecar string.");
                return item.GetString();
            }
            var track = matches[0];
            return new(Text(track, "title"), Text(track, "mixName"), Text(track.GetProperty("normalized"), "isrc"),
                Text(track, "providerTrackId"), Text(root.GetProperty("identity"), "provider"),
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        { throw Error(409, "media_sidecar_invalid", "Release sidecar is malformed; no link was prepared/applied."); }
        catch (IOException) { throw Error(409, "media_sidecar_unavailable", "Sidecar could not be read; no link was saved."); }
        catch (UnauthorizedAccessException) { throw Error(409, "media_sidecar_unavailable", "Sidecar read access was denied; no link was saved."); }
    }
    public async Task<MediaLinkReview> PrepareAsync(long id, string recordingId, CancellationToken ct)
    {
        await Ready(ct);
        var media = await Item(id, ct);
        var candidates = await CandidatesAsync(id, ct);
        if (!candidates.Candidates.Any(c => c.RecordingId == recordingId))
            throw Error(400, "media_candidate_unavailable", "Choose a server-derived metadata candidate.");
        if (candidates.LinkedRecordingId is not null) throw Error(409, "media_already_linked", "This scanner file is already linked; replacement is a separate reviewed operation.");
        var verified = await Verify(media, ct);
        var sidecar = await ReadSidecar(media, ct);
        await using var unit = await storage.BeginUnitOfWorkAsync(ct);
        var recording = await ReadRecording(unit, recordingId, ct);
        var candidate = Compare(media, recording, sidecar);
        if (candidate.Blockers.Count > 0) throw Error(409, "media_identity_conflict", string.Join(" ", candidate.Blockers));
        if (await unit.Matches.GetBestMatchByMediaFileIdAsync(MediaId(id), ct) is not null) throw Error(409, "media_already_linked", "Media was linked during preparation.");
        if (await unit.MediaFiles.GetByPathAsync(media.Path, ct) is not null)
            throw Error(409, "media_already_linked", "This path is already registered; re-association requires a separate review.");
        if (Json(await Item(id, ct)) != Json(media)) throw Error(412, "enrichment_state_changed", "Scanner metadata changed during preparation.");
        var review = new MediaLinkReview(Guid.NewGuid().ToString("N"), "", media, candidate, clock.GetUtcNow(), verified.Hash, sidecar?.Title, sidecar?.Mix);
        review = review with { Version = Hash(new LinkPayload("MediaLink", SourceId, review, recording, sidecar?.Hash)) };
        await unit.Enrichment!.SaveReviewAsync(new(review.Id, CatalogEntityKind.Recording, recordingId, review.Version,
            Json(new LinkPayload("MediaLink", SourceId, review, recording, sidecar?.Hash)), review.CreatedAt), ct);
        CheckVerifiedFile(media, verified.Modified);
        if ((await ReadSidecar(media, ct))?.Hash != sidecar?.Hash)
            throw Error(412, "enrichment_state_changed", "Sidecar changed during preparation; nothing was saved.");
        await unit.CommitAsync(ct);
        return review;
    }
    private static async Task<LinkPayload> Load(IUnitOfWork unit, string id, CancellationToken ct)
    {
        var stored = await unit.Enrichment!.GetReviewAsync(id, ct) ?? throw Error(404, "media_review_not_found", "Media link review was not found.");
        using var document = JsonDocument.Parse(stored.PayloadJson);
        if (!document.RootElement.TryGetProperty("Type", out var type) || type.GetString() != "MediaLink")
            throw Error(400, "media_review_invalid", "This is not a media link review.");
        return JsonSerializer.Deserialize<LinkPayload>(stored.PayloadJson) ?? throw new InvalidDataException("Invalid stored media review.");
    }
    public async Task<MediaLinkReview> GetReviewAsync(string id, CancellationToken ct)
    {
        await Ready(ct);
        await using var unit = await storage.BeginUnitOfWorkAsync(ct);
        var payload = await Load(unit, id, ct);
        var audit = await unit.Enrichment!.GetAuditAsync(id, ct);
        return payload.Review with { Outcome = audit is null ? null : JsonSerializer.Deserialize<MediaLinkOutcome>(audit.AfterJson) };
    }
    public async Task<MediaLinkOutcome> ApplyAsync(string id, string version, string reason, bool confirmed, CancellationToken ct)
    {
        if (!confirmed || string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > 2000)
            throw Error(400, "validation_failed", "Give a bounded review reason and explicitly confirm file/Recording version identity.");
        await Ready(ct);
        LinkPayload payload;
        await using (var read = await storage.BeginUnitOfWorkAsync(ct))
        {
            payload = await Load(read, id, ct);
            if (payload.Review.Version != version) throw Error(412, "enrichment_state_changed", "Use the original media review ETag.");
            var audit = await read.Enrichment!.GetAuditAsync(id, ct);
            if (audit is not null)
            {
                if (audit.Reason != reason.Trim()) throw Error(409, "enrichment_already_applied", "This link review was applied with a different reason.");
                return JsonSerializer.Deserialize<MediaLinkOutcome>(audit.AfterJson)!;
            }
        }
        if (clock.GetUtcNow() - payload.Review.CreatedAt >= TimeSpan.FromHours(24)) throw Error(410, "enrichment_review_expired", "Media review expired; prepare a fresh review.");
        if (payload.SourceId != SourceId) throw Error(409, "media_source_changed", "Scanner identity/configuration changed; prepare a fresh review.");
        var media = await Item(payload.Review.Media.Id, ct);
        if (Json(media) != Json(payload.Review.Media)) throw Error(412, "enrichment_state_changed", "Scanner metadata changed; prepare a fresh review.");
        var verified = await Verify(media, ct, payload.Review.VerifiedSha256);
        var sidecar = await ReadSidecar(media, ct);
        if (sidecar?.Hash != payload.SidecarHash) throw Error(412, "enrichment_state_changed", "Release sidecar changed; prepare a fresh review.");
        await using var unit = await storage.BeginUnitOfWorkAsync(ct);
        var recording = await ReadRecording(unit, payload.Recording.Recording.Id, ct);
        if (Json(recording) != Json(payload.Recording)) throw Error(412, "enrichment_state_changed", "Catalog Recording or Artist evidence changed; nothing linked.");
        if (Json(await Item(media.Id, ct)) != Json(media))
            throw Error(412, "enrichment_state_changed", "Scanner evidence changed during verification; nothing linked.");
        var existingAudit = await unit.Enrichment!.GetAuditAsync(id, ct);
        if (existingAudit is not null)
        {
            if (existingAudit.Reason != reason.Trim()) throw Error(409, "enrichment_already_applied", "Review already applied with a different reason.");
            return JsonSerializer.Deserialize<MediaLinkOutcome>(existingAudit.AfterJson)!;
        }
        var outcome = await SaveLink(unit, payload, reason, ct);
        CheckVerifiedFile(media, verified.Modified);
        if ((await ReadSidecar(media, ct))?.Hash != payload.SidecarHash || Json(await Item(media.Id, ct)) != Json(media) || payload.SourceId != SourceId)
            throw Error(412, "enrichment_state_changed", "File sidecar or scanner evidence changed during linking; nothing was saved.");
        await unit.CommitAsync(ct);
        return outcome;
    }
    private async Task<MediaLinkOutcome> SaveLink(IUnitOfWork unit, LinkPayload payload, string reason, CancellationToken ct)
    {
        var media = payload.Review.Media;
        var id = payload.Review.Id;
        var recording = payload.Recording;
        var mediaId = MediaId(media.Id);
        if (await unit.Matches.GetBestMatchByMediaFileIdAsync(mediaId, ct) is not null ||
            await unit.MediaFiles.GetByIdAsync(mediaId, ct) is not null || await unit.MediaFiles.GetByPathAsync(media.Path, ct) is not null)
            throw Error(409, "media_already_linked", "This media identity is already registered; no existing association is overwritten.");
        if (await unit.MediaFiles.HasConflictingRecordingMatchAsync(payload.Review.VerifiedSha256, recording.Recording.Id, ct))
            throw Error(409, "media_identity_conflict", "Identical verified bytes are already associated with another Recording; review those identities first.");
        var now = clock.GetUtcNow();
        await unit.MediaFiles.UpsertAsync(new MediaFile { Id = mediaId, Path = media.Path, ContentHash = payload.Review.VerifiedSha256,
            MetadataJson = Json(media), SourcePayloadJson = Json(new { ScannerIdentity = SourceId, ScannerMediaId = media.Id, ReviewId = id }),
            CreatedUtc = now, UpdatedUtc = now }, ct);
        await unit.Matches.UpsertBestMatchAsync(new MatchRecord { MediaFileId = mediaId, RecordingId = recording.Recording.Id,
            OverrideState = MatchOverrideState.Manual, Score = 0, Confidence = 0,
            EvidenceJson = Json(new { ReviewId = id, Candidate = payload.Review.Candidate, Authority = "ExplicitUserReview" }), CreatedUtc = now, UpdatedUtc = now }, ct);
        var outcome = new MediaLinkOutcome(id, mediaId, recording.Recording.Id, now);
        await unit.Enrichment!.SaveAuditAsync(new(id, Json(new[] { "MediaRecordingLink" }), reason.Trim(), Json(payload), Json(outcome), now), ct);
        return outcome;
    }
}
