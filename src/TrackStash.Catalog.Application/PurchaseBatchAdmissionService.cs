using System.Globalization;
using TrackStash.Core.Storage;

namespace TrackStash.Catalog.Application;

/// <summary>Outcome of evaluating (and optionally admitting) one purchase release in a batch.</summary>
/// <param name="Status">admitted, would_admit, needs_review or failed.</param>
/// <param name="Reasons">Why the release needs individual review or failed; empty when admitted.</param>
public sealed record PurchaseBatchAdmissionItem(long ReleaseId, string Title, string Status, IReadOnlyList<string> Reasons,
    int Tracks, int NewArtists, int UnresolvedArtists, int NewRecordings, int ReusedRecordings, string? CatalogReleaseId);

/// <summary>Batch admission results in request order.</summary>
public sealed record PurchaseBatchAdmissionReport(bool DryRun, IReadOnlyList<PurchaseBatchAdmissionItem> Items)
{
    public int Admitted => Items.Count(i => i.Status == PurchaseBatchAdmissionService.Admitted);
    public int WouldAdmit => Items.Count(i => i.Status == PurchaseBatchAdmissionService.WouldAdmit);
    public int NeedsReview => Items.Count(i => i.Status == PurchaseBatchAdmissionService.NeedsReview);
    public int Failed => Items.Count(i => i.Status == PurchaseBatchAdmissionService.Failed);
}

public interface IPurchaseBatchAdmissionService
{
    /// <summary>Ready purchase releases whose Beatport release ID no catalog Release owns yet, in corpus order.</summary>
    ValueTask<IReadOnlyList<long>> ReadyReleaseIdsAsync(CancellationToken cancellationToken = default);
    /// <summary>Admits each release whose identities are all clear-cut; a dry run stores nothing.</summary>
    ValueTask<PurchaseBatchAdmissionReport> RunAsync(IReadOnlyList<long> releaseIds, bool dryRun, CancellationToken cancellationToken = default);
}

/// <summary>
/// Applies the normal reviewed import to each release using fixed rules: new Beatport identities with no catalog
/// match are created, compound artist names are created as unresolved, a Recording with the same ISRC and mix is reused.
/// Anything else (same-name catalog Artist or Release without a Beatport ID, ambiguous or ISRC-less Recording matches,
/// repeated listings, label or source blockers) leaves the release for individual review. Each release is its own
/// immutable plan and atomic apply, so a stale or blocked release never affects the others.
/// </summary>
public sealed class PurchaseBatchAdmissionService(IReleaseImportService imports, IPurchaseReleaseInspector inspector) : IPurchaseBatchAdmissionService
{
    public const int MaxReleasesPerRequest = 25;
    public const string Admitted = "admitted", WouldAdmit = "would_admit", NeedsReview = "needs_review", Failed = "failed";
    public const string BatchReason = "Batch admission of ready purchase releases: Beatport identities accepted under the batch rules; exceptions were left for individual review.";
    public const string NewArtistEvidence = "Batch admission: new Beatport artist with no same-name catalog artist.";
    public const string CompoundEvidence = "Batch admission: compound Beatport artist imported as unresolved for later splitting.";
    public const string NewRecordingEvidence = "Batch admission: new Beatport recording with no catalog match.";
    public const string SameIsrcEvidence = "Batch admission: existing Recording with the same ISRC and mix.";
    public const string LegacyOwnerEvidence = "Batch admission: existing Recording already holding this Beatport track ID.";
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public async ValueTask<IReadOnlyList<long>> ReadyReleaseIdsAsync(CancellationToken cancellationToken = default)
    {
        var ids = new List<long>();
        for (var page = 1; ; page++)
        {
            var result = await inspector.QueryAsync(new(page, 100), hideAdmitted: true, readyOnly: true, cancellationToken).ConfigureAwait(false);
            ids.AddRange(result.Items.Select(i => i.ReleaseId));
            if (result.Items.Count == 0 || ids.Count >= result.Total) return ids;
        }
    }

    public async ValueTask<PurchaseBatchAdmissionReport> RunAsync(IReadOnlyList<long> releaseIds, bool dryRun, CancellationToken cancellationToken = default)
    {
        if (releaseIds.Count is 0 or > MaxReleasesPerRequest || releaseIds.Any(id => id <= 0) || releaseIds.Distinct().Count() != releaseIds.Count)
            throw new ReleaseImportException(400, "validation_failed", $"Send 1-{MaxReleasesPerRequest} distinct positive release IDs.");
        if (!await Gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            throw new ReleaseImportException(409, "batch_in_progress", "Another batch admission is running. Wait for it to finish.");
        try
        {
            var items = new List<PurchaseBatchAdmissionItem>();
            foreach (var id in releaseIds) items.Add(await OneAsync(id, dryRun, cancellationToken).ConfigureAwait(false));
            return new(dryRun, items);
        }
        finally { Gate.Release(); }
    }

    private async ValueTask<PurchaseBatchAdmissionItem> OneAsync(long releaseId, bool dryRun, CancellationToken ct)
    {
        var title = "";
        try
        {
            var first = await imports.PlanAsync(releaseId, [], persist: false, ct).ConfigureAwait(false);
            title = first.Source.Title ?? "";
            if (first.ExistingRelease is not null)
                return Item(first, NeedsReview, [first.NoOp ? "Already admitted with unchanged source." : "Already admitted; the source has changed since."]);
            var (decisions, reasons) = Decide(first);
            if (reasons.Count > 0) return Item(first, NeedsReview, reasons);
            var plan = await imports.PlanAsync(releaseId, decisions, persist: !dryRun, ct).ConfigureAwait(false);
            if (plan.Blockers.Count > 0) return Item(plan, NeedsReview, plan.Blockers);
            if (dryRun) return Item(plan, WouldAdmit, []);
            var outcome = await imports.ApplyAsync(plan.OperationId, plan.Version, BatchReason, confirmed: true, ct).ConfigureAwait(false);
            return Item(plan, Admitted, [], outcome.ReleaseId);
        }
        catch (ReleaseImportException error)
        {
            return new(releaseId, title, Failed, [error.Message], 0, 0, 0, 0, 0, null);
        }
    }

    private static PurchaseBatchAdmissionItem Item(ReleaseImportPlan plan, string status, IReadOnlyList<string> reasons, string? catalogReleaseId = null) =>
        new(plan.Source.ReleaseId, plan.Source.Title ?? "", status, reasons, plan.Placements.Count,
            plan.Artists.Count(a => a.Before is null && a.After.IdentityStatus != "unresolved_compound"),
            plan.Artists.Count(a => a.After.IdentityStatus == "unresolved_compound" && a.Before?.IdentityStatus != "unresolved_compound"),
            plan.Recordings.Count(r => r.Before is null), plan.Recordings.Count(r => r.Before is not null),
            catalogReleaseId ?? plan.ExistingRelease?.Id);

    internal static (List<ReleaseImportDecision> Decisions, List<string> Reasons) Decide(ReleaseImportPlan plan)
    {
        var decisions = new List<ReleaseImportDecision>();
        var reasons = new List<string>();
        static string Names(IEnumerable<ReleaseImportCandidate> candidates) => string.Join(", ", candidates.Select(c => $"'{c.Name}'"));
        foreach (var identity in plan.Identities)
        {
            var id = identity.ProviderId;
            switch (identity.Kind)
            {
                case "release" when identity.Candidates.Count > 0:
                    reasons.Add($"A catalog Release with the same title and no Beatport ID exists ({Names(identity.Candidates)}).");
                    break;
                case "artist" when identity.Action == "reuse":
                    if (identity.CompoundReview && plan.Blockers.Any(b => b.StartsWith($"Artist {id.ToString(CultureInfo.InvariantCulture)}: {ReleaseImportService.UnreviewedCompoundBlocker}", StringComparison.Ordinal)))
                        decisions.Add(new("artist", id, "unresolved", Evidence: CompoundEvidence));
                    break;
                case "artist" when identity.Action == "undecided":
                    if (identity.Candidates.Count > 0)
                        reasons.Add($"Artist '{identity.Name}' ({id}) has the same name as catalog Artist {Names(identity.Candidates)} without a Beatport ID.");
                    else decisions.Add(identity.CompoundReview ? new("artist", id, "unresolved", Evidence: CompoundEvidence)
                        : new("artist", id, "create", Evidence: NewArtistEvidence));
                    break;
                case "recording" when identity.Action == "undecided":
                    var recording = DecideRecording(plan, identity);
                    if (recording.Decision is not null) decisions.Add(recording.Decision);
                    else reasons.Add(recording.Reason!);
                    break;
            }
        }
        return (decisions, reasons);
    }

    private static (ReleaseImportDecision? Decision, string? Reason) DecideRecording(ReleaseImportPlan plan, ReleaseImportIdentity track)
    {
        var id = track.ProviderId;
        var label = $"Track '{track.Name}'{(track.MixName is null ? "" : $" ({track.MixName})")} [{id}]";
        if (plan.Warnings.Any(w => w.StartsWith($"Track {id.ToString(CultureInfo.InvariantCulture)}: {ReleaseImportService.SharedListingWarning}", StringComparison.Ordinal)))
            return (null, $"{label} shares an ISRC or title and version with another track on this release.");
        if (track.Candidates.Count == 0) return (new("recording", id, "create", Evidence: NewRecordingEvidence), null);
        bool Compatible(ReleaseImportCandidate c) => c.MixName == track.MixName && !(c.Isrc is not null && track.Isrc is not null && c.Isrc != track.Isrc);
        var owners = track.Candidates.Where(c => c.BeatportIds?.Contains(id.ToString(CultureInfo.InvariantCulture)) == true).ToList();
        if (owners.Count > 0)
            return owners.Count == 1 && Compatible(owners[0])
                ? (new("recording", id, "reuse", owners[0].Id, LegacyOwnerEvidence), null)
                : (null, $"{label} is already referenced by an existing Recording with a different version.");
        var sameMaster = track.Candidates.Where(c => track.Isrc is not null && c.Isrc == track.Isrc && c.MixName == track.MixName).ToList();
        return sameMaster.Count switch
        {
            1 => (new("recording", id, "reuse", sameMaster[0].Id, SameIsrcEvidence), null),
            > 1 => (null, $"{label}: several catalog Recordings share its ISRC and mix."),
            _ => (null, $"{label} may match catalog Recording {string.Join(", ", track.Candidates.Select(c => $"'{c.Name}'"))} but there is no ISRC to confirm it."),
        };
    }
}
