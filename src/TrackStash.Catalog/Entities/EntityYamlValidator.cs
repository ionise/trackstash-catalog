using YamlDotNet.RepresentationModel;

namespace TrackStash.Catalog.Entities;

public sealed record EntityValidationIssue(
    string Severity,
    string Path,
    string Message);

public sealed record EntityValidationResult(
    bool IsValid,
    int DocumentCount,
    IReadOnlyList<EntityValidationIssue> Issues)
{
    public int ErrorCount => Issues.Count(i => string.Equals(i.Severity, "error", StringComparison.OrdinalIgnoreCase));
    public int WarningCount => Issues.Count(i => string.Equals(i.Severity, "warning", StringComparison.OrdinalIgnoreCase));
}

public static class EntityYamlValidator
{
    private static readonly HashSet<string> AllowedKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "Label", "Artist", "Release", "Recording",
    };

    private static readonly HashSet<string> AllowedModes = new(StringComparer.OrdinalIgnoreCase)
    {
        "replace", "merge", "create-only", "update-only",
    };

    public static EntityValidationResult Validate(string yaml)
    {
        var issues = new List<EntityValidationIssue>();

        var stream = new YamlStream();
        try
        {
            using var reader = new StringReader(yaml);
            stream.Load(reader);
        }
        catch (Exception ex)
        {
            issues.Add(new EntityValidationIssue("error", "$", $"Invalid YAML: {ex.Message}"));
            return new EntityValidationResult(false, 0, issues);
        }

        if (stream.Documents.Count == 0)
        {
            issues.Add(new EntityValidationIssue("error", "$", "YAML does not contain any documents."));
            return new EntityValidationResult(false, 0, issues);
        }

        for (var i = 0; i < stream.Documents.Count; i++)
            ValidateDocument(stream.Documents[i], i, issues);

        var isValid = issues.All(i => !string.Equals(i.Severity, "error", StringComparison.OrdinalIgnoreCase));
        return new EntityValidationResult(isValid, stream.Documents.Count, issues);
    }

    private static void ValidateDocument(YamlDocument document, int index, List<EntityValidationIssue> issues)
    {
        var prefix = $"$[{index}]";
        if (document.RootNode is not YamlMappingNode root)
        {
            issues.Add(new EntityValidationIssue("error", prefix, "Document root must be a mapping object."));
            return;
        }

        var apiVersion = GetScalar(root, "apiVersion");
        var kind = GetScalar(root, "kind");
        var mode = GetScalar(root, "mode");
        var metadata = GetMap(root, "metadata");
        var spec = GetMap(root, "spec");

        if (string.IsNullOrWhiteSpace(apiVersion))
            issues.Add(new EntityValidationIssue("error", $"{prefix}.apiVersion", "apiVersion is required."));
        else if (!string.Equals(apiVersion, "catalog.trackstash/v1", StringComparison.OrdinalIgnoreCase))
            issues.Add(new EntityValidationIssue("error", $"{prefix}.apiVersion", "apiVersion must be 'catalog.trackstash/v1'."));

        if (string.IsNullOrWhiteSpace(kind))
            issues.Add(new EntityValidationIssue("error", $"{prefix}.kind", "kind is required."));
        else if (!AllowedKinds.Contains(kind))
            issues.Add(new EntityValidationIssue("error", $"{prefix}.kind", "kind must be one of: Label, Artist, Release, Recording."));

        if (string.IsNullOrWhiteSpace(mode))
            issues.Add(new EntityValidationIssue("error", $"{prefix}.mode", "mode is required."));
        else if (!AllowedModes.Contains(mode))
            issues.Add(new EntityValidationIssue("error", $"{prefix}.mode", "mode must be one of: replace, merge, create-only, update-only."));

        if (spec is null)
        {
            issues.Add(new EntityValidationIssue("error", $"{prefix}.spec", "spec is required and must be a mapping object."));
            return;
        }

        var metadataId = metadata is null ? null : GetScalar(metadata, "id");
        var specId = GetScalar(spec, "id");

        var identityOk = !string.IsNullOrWhiteSpace(metadataId)
            || !string.IsNullOrWhiteSpace(specId)
            || HasAnyExternalReference(spec);

        if (!identityOk)
            issues.Add(new EntityValidationIssue("error", $"{prefix}.spec", "Entity identity is required: set metadata.id, spec.id, or at least one spec.externalRefs entry."));

        ValidateCommonCollections(spec, prefix, issues);

        switch (kind?.Trim())
        {
            case "Label":
                ValidateLabel(spec, prefix, issues);
                break;
            case "Artist":
                ValidateArtist(spec, prefix, issues);
                break;
            case "Release":
                ValidateRelease(spec, prefix, issues);
                break;
            case "Recording":
                ValidateRecording(spec, prefix, issues);
                break;
        }
    }

    private static void ValidateLabel(YamlMappingNode spec, string prefix, List<EntityValidationIssue> issues)
    {
        var name = GetScalar(spec, "name");
        if (string.IsNullOrWhiteSpace(name))
            issues.Add(new EntityValidationIssue("error", $"{prefix}.spec.name", "Label requires spec.name."));
    }

    private static void ValidateArtist(YamlMappingNode spec, string prefix, List<EntityValidationIssue> issues)
    {
        var name = GetScalar(spec, "name");
        if (string.IsNullOrWhiteSpace(name))
            issues.Add(new EntityValidationIssue("error", $"{prefix}.spec.name", "Artist requires spec.name."));
    }

    private static void ValidateRelease(YamlMappingNode spec, string prefix, List<EntityValidationIssue> issues)
    {
        var hasName = !string.IsNullOrWhiteSpace(GetScalar(spec, "name"));
        var hasTitle = !string.IsNullOrWhiteSpace(GetScalar(spec, "title"));
        if (!hasName && !hasTitle)
            issues.Add(new EntityValidationIssue("error", $"{prefix}.spec", "Release requires spec.title or spec.name."));

        var credits = GetSeq(spec, "artistCredits");
        if (credits is not null)
            ValidateIdentityArray(credits, $"{prefix}.spec.artistCredits", issues, "artistId", "artistRef", "Release artist credit");

        var labels = GetSeq(spec, "labelLinks");
        if (labels is not null)
            ValidateIdentityArray(labels, $"{prefix}.spec.labelLinks", issues, "labelId", "labelRef", "Release label link");

        var recordings = GetSeq(spec, "recordings");
        if (recordings is not null)
            ValidateIdentityArray(recordings, $"{prefix}.spec.recordings", issues, "recordingId", "recordingRef", "Release recording link");
    }

    private static void ValidateRecording(YamlMappingNode spec, string prefix, List<EntityValidationIssue> issues)
    {
        var hasName = !string.IsNullOrWhiteSpace(GetScalar(spec, "name"));
        var hasTitle = !string.IsNullOrWhiteSpace(GetScalar(spec, "title"));
        if (!hasName && !hasTitle)
            issues.Add(new EntityValidationIssue("error", $"{prefix}.spec", "Recording requires spec.title or spec.name."));

        var credits = GetSeq(spec, "artistCredits");
        if (credits is not null)
            ValidateIdentityArray(credits, $"{prefix}.spec.artistCredits", issues, "artistId", "artistRef", "Recording artist credit");

        var releases = GetSeq(spec, "releaseLinks");
        if (releases is not null)
            ValidateIdentityArray(releases, $"{prefix}.spec.releaseLinks", issues, "releaseId", "releaseRef", "Recording release link");

        var relationships = GetSeq(spec, "relationships");
        if (relationships is not null)
        {
            var idx = 0;
            foreach (var item in relationships)
            {
                if (item is not YamlMappingNode map)
                {
                    issues.Add(new EntityValidationIssue("error", $"{prefix}.spec.relationships[{idx}]", "Relationship entry must be a mapping object."));
                    idx++;
                    continue;
                }

                var hasTargetId = !string.IsNullOrWhiteSpace(GetScalar(map, "relatedRecordingId"));
                var hasTargetRef = GetMap(map, "relatedRecordingRef") is not null;
                if (!hasTargetId && !hasTargetRef)
                    issues.Add(new EntityValidationIssue("error", $"{prefix}.spec.relationships[{idx}]", "Relationship requires relatedRecordingId or relatedRecordingRef."));

                if (string.IsNullOrWhiteSpace(GetScalar(map, "relationshipType")))
                    issues.Add(new EntityValidationIssue("error", $"{prefix}.spec.relationships[{idx}].relationshipType", "relationshipType is required."));

                idx++;
            }
        }
    }

    private static void ValidateCommonCollections(YamlMappingNode spec, string prefix, List<EntityValidationIssue> issues)
    {
        var aliases = GetSeq(spec, "aliases");
        if (aliases is not null)
        {
            var idx = 0;
            foreach (var item in aliases)
            {
                if (item is not YamlMappingNode map)
                {
                    issues.Add(new EntityValidationIssue("error", $"{prefix}.spec.aliases[{idx}]", "Alias entry must be a mapping object."));
                    idx++;
                    continue;
                }

                if (string.IsNullOrWhiteSpace(GetScalar(map, "value")))
                    issues.Add(new EntityValidationIssue("error", $"{prefix}.spec.aliases[{idx}].value", "Alias value is required."));
                idx++;
            }
        }

        var refs = GetSeq(spec, "externalRefs");
        if (refs is not null)
        {
            var idx = 0;
            foreach (var item in refs)
            {
                if (item is not YamlMappingNode map)
                {
                    issues.Add(new EntityValidationIssue("error", $"{prefix}.spec.externalRefs[{idx}]", "External ref entry must be a mapping object."));
                    idx++;
                    continue;
                }

                if (string.IsNullOrWhiteSpace(GetScalar(map, "source")))
                    issues.Add(new EntityValidationIssue("error", $"{prefix}.spec.externalRefs[{idx}].source", "source is required."));
                if (string.IsNullOrWhiteSpace(GetScalar(map, "externalId")))
                    issues.Add(new EntityValidationIssue("error", $"{prefix}.spec.externalRefs[{idx}].externalId", "externalId is required."));
                idx++;
            }
        }
    }

    private static void ValidateIdentityArray(
        YamlSequenceNode seq,
        string path,
        List<EntityValidationIssue> issues,
        string idField,
        string refField,
        string entryLabel)
    {
        var idx = 0;
        foreach (var item in seq)
        {
            if (item is not YamlMappingNode map)
            {
                issues.Add(new EntityValidationIssue("error", $"{path}[{idx}]", $"{entryLabel} must be a mapping object."));
                idx++;
                continue;
            }

            var hasId = !string.IsNullOrWhiteSpace(GetScalar(map, idField));
            var hasRef = GetMap(map, refField) is not null;
            if (!hasId && !hasRef)
                issues.Add(new EntityValidationIssue("error", $"{path}[{idx}]", $"{entryLabel} requires {idField} or {refField}."));

            idx++;
        }
    }

    private static bool HasAnyExternalReference(YamlMappingNode spec)
    {
        var refs = GetSeq(spec, "externalRefs");
        if (refs is null)
            return false;

        foreach (var item in refs)
        {
            if (item is not YamlMappingNode map)
                continue;

            var source = GetScalar(map, "source");
            var externalId = GetScalar(map, "externalId");
            if (!string.IsNullOrWhiteSpace(source) && !string.IsNullOrWhiteSpace(externalId))
                return true;
        }

        return false;
    }

    private static string? GetScalar(YamlMappingNode map, string key)
    {
        if (!map.Children.TryGetValue(new YamlScalarNode(key), out var node))
            return null;

        return (node as YamlScalarNode)?.Value;
    }

    private static YamlMappingNode? GetMap(YamlMappingNode map, string key)
    {
        if (!map.Children.TryGetValue(new YamlScalarNode(key), out var node))
            return null;

        return node as YamlMappingNode;
    }

    private static YamlSequenceNode? GetSeq(YamlMappingNode map, string key)
    {
        if (!map.Children.TryGetValue(new YamlScalarNode(key), out var node))
            return null;

        return node as YamlSequenceNode;
    }
}
