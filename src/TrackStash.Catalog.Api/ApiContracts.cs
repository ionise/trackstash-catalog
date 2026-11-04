using System.ComponentModel.DataAnnotations;

namespace TrackStash.Catalog.Api;

/// <summary>Fields accepted when creating a label. Identity and timestamps are server assigned.</summary>
public sealed record CreateLabelRequest
{
    [Required, MinLength(1)]
    public required string Name { get; init; }
    public string? SortName { get; init; }
}

/// <summary>A patch of scalar fields or a full profile replacement. Omitted properties are preserved; profile cannot be null.</summary>
public sealed record UpdateLabelRequest
{
    public System.Text.Json.JsonElement Name { get; init; }
    public System.Text.Json.JsonElement SortName { get; init; }
    public System.Text.Json.JsonElement Profile { get; init; }
}

/// <summary>A manually curated web link; sourceUrl records provenance and no URL is fetched by this API.</summary>
public sealed record LabelWebLinkResponse(string Kind, string Url, string? Title, string? SourceUrl);

/// <summary>Curated label profile. All fields are required for replacement; optional values can be null.</summary>
public sealed record LabelProfileResponse(
    string? Description, string? CountryCode, string? City, int? FoundedYear, string LifecycleStatus,
    string? Notes, bool Favourite, string ReviewStatus, IReadOnlyList<LabelWebLinkResponse> Links);

/// <summary>A lightweight label with its opaque aggregate version.</summary>
public sealed record LabelListItemResponse(string Id, string? Name, string? SortName, string Version);

/// <summary>A page of labels and the matching count before pagination.</summary>
public sealed record LabelListResponse(IReadOnlyList<LabelListItemResponse> Items, int Total, int Page, int PageSize);

/// <summary>An alternate name for the same canonical label.</summary>
public sealed record LabelAliasResponse(string Value, bool IsPrimary);

/// <summary>A label's provider identity without raw provider payloads.</summary>
public sealed record LabelReferenceResponse(string Source, string ExternalId, bool IsPrimary, DateTimeOffset? LastSeenUtc);

/// <summary>Canonical label detail. Version is opaque; use the response ETag for conditional edits.</summary>
public sealed record LabelResponse(
    string Id, string? Name, string? SortName, string Version,
    DateTimeOffset? CreatedUtc, DateTimeOffset? UpdatedUtc,
    IReadOnlyList<LabelAliasResponse> Aliases, IReadOnlyList<LabelReferenceResponse> ExternalReferences,
    LabelProfileResponse Profile);
