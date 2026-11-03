using System.ComponentModel.DataAnnotations;

namespace TrackStash.Catalog.Api;

/// <summary>Fields accepted when creating a label. Identity and timestamps are server assigned.</summary>
public sealed record CreateLabelRequest
{
    [Required, MinLength(1)]
    public required string Name { get; init; }
    public string? SortName { get; init; }
}

/// <summary>A merge patch. Omitted properties are preserved; sortName alone may be null.</summary>
public sealed record UpdateLabelRequest
{
    public System.Text.Json.JsonElement Name { get; init; }
    public System.Text.Json.JsonElement SortName { get; init; }
}

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
    IReadOnlyList<LabelAliasResponse> Aliases, IReadOnlyList<LabelReferenceResponse> ExternalReferences);
