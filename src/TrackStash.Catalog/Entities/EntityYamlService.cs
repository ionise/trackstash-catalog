using TrackStash.Core.Identifiers;
using TrackStash.Core.Normalization;
using TrackStash.Core.Storage;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace TrackStash.Catalog.Entities;

public sealed record EntityApplyResult(
    bool Success,
    string Kind,
    string Mode,
    string EntityId,
    bool DryRun,
    IReadOnlyList<string> Actions,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors);

public static class EntityYamlService
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .Build();

    public static EntityEnvelope ParseSingleDocument(string yaml)
    {
        var envelope = Deserializer.Deserialize<EntityEnvelope>(yaml);
        if (envelope is null)
            throw new ArgumentException("YAML document is empty.");

        return envelope;
    }

    public static string ToYaml(EntityEnvelope envelope) => Serializer.Serialize(envelope);

    public static async Task<EntityApplyResult> ApplyAsync(
        EntityEnvelope envelope,
        IStorageProvider provider,
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        var warnings = new List<string>();
        var errors = new List<string>();
        var actions = new List<string>();

        if (!string.Equals(envelope.ApiVersion, "catalog.trackstash/v1", StringComparison.OrdinalIgnoreCase))
            errors.Add("apiVersion must be catalog.trackstash/v1.");

        var mode = (envelope.Mode ?? string.Empty).Trim().ToLowerInvariant();
        if (mode is not ("replace" or "merge" or "create-only" or "update-only"))
            errors.Add("mode must be one of replace, merge, create-only, update-only.");

        var kind = (envelope.Kind ?? string.Empty).Trim();
        if (kind is not ("Label" or "Artist" or "Release" or "Recording"))
            errors.Add("kind must be one of Label, Artist, Release, Recording.");

        if (errors.Count > 0)
            return new EntityApplyResult(false, kind, mode, envelope.Spec.Id ?? envelope.Metadata?.Id ?? string.Empty, dryRun, actions, warnings, errors);

        await using var uow = await provider.BeginUnitOfWorkAsync(cancellationToken).ConfigureAwait(false);

        var existingId = await ResolveExistingIdAsync(kind, envelope.Spec, uow, cancellationToken).ConfigureAwait(false);
        var explicitId = FirstNonEmpty(envelope.Spec.Id, envelope.Metadata?.Id);
        var finalId = explicitId ?? existingId ?? EntityId.New();

        if (mode == "create-only" && existingId is not null)
            errors.Add($"Entity already exists for {kind} with id '{existingId}'.");

        if (mode == "update-only" && existingId is null)
            errors.Add($"Entity does not exist for {kind}; update-only requires a prior entity.");

        if (errors.Count > 0)
            return new EntityApplyResult(false, kind, mode, finalId, dryRun, actions, warnings, errors);

        var doReplaceCleanup = string.Equals(mode, "replace", StringComparison.OrdinalIgnoreCase);
        var entityDelete = uow.EntityDelete;

        if (doReplaceCleanup && entityDelete is null)
            throw new InvalidOperationException("replace mode requires IEntityDeleteService from the active storage provider.");

        try
        {
            switch (kind)
            {
                case "Label":
                    {
                        var model = BuildLabelModel(finalId, envelope.Spec);
                        if (doReplaceCleanup)
                        {
                            actions.Add("replace-cleanup: label_alias");
                            actions.Add("replace-cleanup: label_external_ref");
                            if (!dryRun)
                                await entityDelete!.DeleteOwnedRowsAsync("label", finalId, cancellationToken).ConfigureAwait(false);
                        }

                        actions.Add("upsert: label");
                        if (!dryRun)
                            await uow.Labels.UpsertAsync(model, cancellationToken).ConfigureAwait(false);
                        break;
                    }

                case "Artist":
                    {
                        var model = BuildArtistModel(finalId, envelope.Spec);
                        if (doReplaceCleanup)
                        {
                            actions.Add("replace-cleanup: artist_alias");
                            actions.Add("replace-cleanup: artist_external_ref");
                            if (!dryRun)
                                await entityDelete!.DeleteOwnedRowsAsync("artist", finalId, cancellationToken).ConfigureAwait(false);
                        }

                        actions.Add("upsert: artist");
                        if (!dryRun)
                            await uow.Artists.UpsertAsync(model, cancellationToken).ConfigureAwait(false);
                        break;
                    }

                case "Release":
                    {
                        var model = await BuildReleaseModelAsync(finalId, envelope.Spec, uow, warnings, cancellationToken).ConfigureAwait(false);
                        if (doReplaceCleanup)
                        {
                            actions.Add("replace-cleanup: release_external_ref");
                            actions.Add("replace-cleanup: release_artist_credit");
                            actions.Add("replace-cleanup: release_label_link");
                            if (!dryRun)
                                await entityDelete!.DeleteOwnedRowsAsync("release", finalId, cancellationToken).ConfigureAwait(false);
                        }

                        actions.Add("upsert: release");
                        if (!dryRun)
                            await uow.Releases.UpsertAsync(model, cancellationToken).ConfigureAwait(false);

                        if (envelope.Spec.Recordings.Count > 0)
                            warnings.Add("release.spec.recordings is currently ignored by apply-entity; manage release links via Recording.releaseLinks.");
                        break;
                    }

                case "Recording":
                    {
                        var model = await BuildRecordingModelAsync(finalId, envelope.Spec, uow, cancellationToken).ConfigureAwait(false);
                        if (doReplaceCleanup)
                        {
                            actions.Add("replace-cleanup: recording_external_ref");
                            actions.Add("replace-cleanup: recording_artist_credit");
                            actions.Add("replace-cleanup: release_recording(by recording)");
                            actions.Add("replace-cleanup: recording_relationship(outgoing)");
                            if (!dryRun)
                                await entityDelete!.DeleteOwnedRowsAsync("recording", finalId, cancellationToken).ConfigureAwait(false);
                        }

                        actions.Add("upsert: recording");
                        if (!dryRun)
                            await uow.Recordings.UpsertAsync(model, cancellationToken).ConfigureAwait(false);
                        break;
                    }
            }

            if (!dryRun)
                await uow.CommitAsync(cancellationToken).ConfigureAwait(false);
            else
                await uow.RollbackAsync(cancellationToken).ConfigureAwait(false);

            return new EntityApplyResult(true, kind, mode, finalId, dryRun, actions, warnings, errors);
        }
        catch (Exception ex)
        {
            errors.Add(ex.Message);
            await uow.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new EntityApplyResult(false, kind, mode, finalId, dryRun, actions, warnings, errors);
        }
    }

    public static async Task<EntityEnvelope?> GetEntityAsync(
        string entityType,
        string entityId,
        IStorageProvider provider,
        CancellationToken cancellationToken = default)
    {
        await using var uow = await provider.BeginUnitOfWorkAsync(cancellationToken).ConfigureAwait(false);

        return entityType.ToLowerInvariant() switch
        {
            "label" => await GetLabelAsync(entityId, uow, cancellationToken).ConfigureAwait(false),
            "artist" => await GetArtistAsync(entityId, uow, cancellationToken).ConfigureAwait(false),
            "release" => await GetReleaseAsync(entityId, uow, cancellationToken).ConfigureAwait(false),
            "recording" => await GetRecordingAsync(entityId, uow, cancellationToken).ConfigureAwait(false),
            _ => null,
        };
    }

    private static async Task<EntityEnvelope?> GetLabelAsync(string id, IUnitOfWork uow, CancellationToken cancellationToken)
    {
        var label = await uow.Labels.GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
        if (label is null) return null;

        var aliases = label.Aliases.Select(a => new EntityAliasSpec
        {
            Value = a.Value,
            NormalizedValue = a.NormalizedValue,
            IsPrimary = a.IsPrimary,
        }).ToList();
        var refs = label.ExternalReferences.Select(r => new EntityRefSpec
        {
            Source = r.Source,
            ExternalId = r.ExternalId,
            IsPrimary = r.IsPrimary,
            LastSeenUtc = r.LastSeenUtc,
            PayloadJson = r.PayloadJson,
        }).ToList();
        return new EntityEnvelope
        {
            Kind = "Label",
            Mode = "replace",
            Metadata = new EntityMetadata { Id = label.Id },
            Spec = new EntitySpec
            {
                Id = label.Id,
                Name = label.Name,
                NormalizedName = label.NormalizedName,
                SortName = label.SortName,
                SourcePayloadJson = label.SourcePayloadJson,
                CreatedUtc = label.CreatedUtc,
                UpdatedUtc = label.UpdatedUtc,
                Aliases = aliases,
                ExternalRefs = refs,
            },
        };
    }

    private static async Task<EntityEnvelope?> GetArtistAsync(string id, IUnitOfWork uow, CancellationToken cancellationToken)
    {
        var artist = await uow.Artists.GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
        if (artist is null) return null;

        var aliases = artist.Aliases.Select(a => new EntityAliasSpec
        {
            Value = a.Value,
            NormalizedValue = a.NormalizedValue,
            IsPrimary = a.IsPrimary,
        }).ToList();
        var refs = artist.ExternalReferences.Select(r => new EntityRefSpec
        {
            Source = r.Source,
            ExternalId = r.ExternalId,
            IsPrimary = r.IsPrimary,
            LastSeenUtc = r.LastSeenUtc,
            PayloadJson = r.PayloadJson,
        }).ToList();
        return new EntityEnvelope
        {
            Kind = "Artist",
            Mode = "replace",
            Metadata = new EntityMetadata { Id = artist.Id },
            Spec = new EntitySpec
            {
                Id = artist.Id,
                Name = artist.Name,
                NormalizedName = artist.NormalizedName,
                SortName = artist.SortName,
                SourcePayloadJson = artist.SourcePayloadJson,
                CreatedUtc = artist.CreatedUtc,
                UpdatedUtc = artist.UpdatedUtc,
                Aliases = aliases,
                ExternalRefs = refs,
            },
        };
    }

    private static async Task<EntityEnvelope?> GetReleaseAsync(string id, IUnitOfWork uow, CancellationToken cancellationToken)
    {
        var release = await uow.Releases.GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
        if (release is null) return null;

        var refs = release.ExternalReferences.Select(r => new EntityRefSpec
        {
            Source = r.Source,
            ExternalId = r.ExternalId,
            IsPrimary = r.IsPrimary,
            LastSeenUtc = r.LastSeenUtc,
            PayloadJson = r.PayloadJson,
        }).ToList();
        var credits = release.ArtistCredits.Select(c => new ArtistCreditSpec
        {
            ArtistId = c.ArtistId,
            CreditName = c.CreditName,
            Position = c.Position,
        }).ToList();
        var labels = release.LabelLinks.Select(l => new LabelLinkSpec
        {
            LabelId = l.LabelId,
            IsPrimary = l.IsPrimary,
            Role = l.Role,
        }).ToList();

        return new EntityEnvelope
        {
            Kind = "Release",
            Mode = "replace",
            Metadata = new EntityMetadata { Id = release.Id },
            Spec = new EntitySpec
            {
                Id = release.Id,
                Name = release.Name,
                NormalizedName = release.NormalizedName,
                Title = release.Title,
                SourcePayloadJson = release.SourcePayloadJson,
                CreatedUtc = release.CreatedUtc,
                UpdatedUtc = release.UpdatedUtc,
                ExternalRefs = refs,
                ArtistCredits = credits,
                LabelLinks = labels,
            },
        };
    }

    private static async Task<EntityEnvelope?> GetRecordingAsync(string id, IUnitOfWork uow, CancellationToken cancellationToken)
    {
        var recording = await uow.Recordings.GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
        if (recording is null) return null;

        var refs = recording.ExternalReferences.Select(r => new EntityRefSpec
        {
            Source = r.Source,
            ExternalId = r.ExternalId,
            IsPrimary = r.IsPrimary,
            LastSeenUtc = r.LastSeenUtc,
            PayloadJson = r.PayloadJson,
        }).ToList();
        var credits = recording.ArtistCredits.Select(c => new ArtistCreditSpec
        {
            ArtistId = c.ArtistId,
            CreditName = c.CreditName,
            Role = c.Role,
            Position = c.Position,
        }).ToList();
        var releaseLinks = recording.ReleaseLinks.Select(l => new ReleaseLinkSpec
        {
            ReleaseId = l.ReleaseId,
            DiscNumber = l.DiscNumber,
            TrackNumber = l.TrackNumber,
        }).ToList();
        var relationships = recording.Relationships.Select(r => new RelationshipSpec
        {
            RelatedRecordingId = r.RelatedRecordingId,
            RelationshipType = r.RelationshipType,
            Source = r.Source,
            Confidence = r.Confidence,
            Notes = r.Notes,
            CreatedUtc = r.CreatedUtc,
            UpdatedUtc = r.UpdatedUtc,
        }).ToList();

        return new EntityEnvelope
        {
            Kind = "Recording",
            Mode = "replace",
            Metadata = new EntityMetadata { Id = recording.Id },
            Spec = new EntitySpec
            {
                Id = recording.Id,
                Name = recording.Name,
                NormalizedName = recording.NormalizedName,
                Title = recording.Title,
                MixName = recording.MixName,
                Isrc = recording.Isrc,
                SourcePayloadJson = recording.SourcePayloadJson,
                CreatedUtc = recording.CreatedUtc,
                UpdatedUtc = recording.UpdatedUtc,
                ExternalRefs = refs,
                ArtistCredits = credits,
                ReleaseLinks = releaseLinks,
                Relationships = relationships,
            },
        };
    }

    private static Label BuildLabelModel(string id, EntitySpec spec) => new()
    {
        Id = id,
        Name = spec.Name,
        NormalizedName = spec.NormalizedName ?? EntityNameNormalizer.NormalizeStrict(spec.Name),
        SortName = spec.SortName,
        SourcePayloadJson = spec.SourcePayloadJson,
        CreatedUtc = spec.CreatedUtc,
        UpdatedUtc = spec.UpdatedUtc ?? DateTimeOffset.UtcNow,
        Aliases = spec.Aliases.Select(a => new EntityAlias
        {
            Value = a.Value,
            NormalizedValue = a.NormalizedValue,
            IsPrimary = a.IsPrimary,
        }).ToArray(),
        ExternalReferences = spec.ExternalRefs.Select(r => new EntityReference
        {
            Source = r.Source,
            ExternalId = r.ExternalId,
            IsPrimary = r.IsPrimary,
            LastSeenUtc = r.LastSeenUtc,
            PayloadJson = r.PayloadJson,
        }).ToArray(),
    };

    private static Artist BuildArtistModel(string id, EntitySpec spec) => new()
    {
        Id = id,
        Name = spec.Name,
        NormalizedName = spec.NormalizedName ?? EntityNameNormalizer.NormalizeStrict(spec.Name),
        SortName = spec.SortName,
        SourcePayloadJson = spec.SourcePayloadJson,
        CreatedUtc = spec.CreatedUtc,
        UpdatedUtc = spec.UpdatedUtc ?? DateTimeOffset.UtcNow,
        Aliases = spec.Aliases.Select(a => new EntityAlias
        {
            Value = a.Value,
            NormalizedValue = a.NormalizedValue,
            IsPrimary = a.IsPrimary,
        }).ToArray(),
        ExternalReferences = spec.ExternalRefs.Select(r => new EntityReference
        {
            Source = r.Source,
            ExternalId = r.ExternalId,
            IsPrimary = r.IsPrimary,
            LastSeenUtc = r.LastSeenUtc,
            PayloadJson = r.PayloadJson,
        }).ToArray(),
    };

    private static async Task<Release> BuildReleaseModelAsync(
        string id,
        EntitySpec spec,
        IUnitOfWork uow,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var credits = new List<ReleaseArtistCredit>();
        foreach (var credit in spec.ArtistCredits)
        {
            var artistId = await ResolveArtistIdAsync(credit.ArtistId, credit.ArtistRef, uow, cancellationToken).ConfigureAwait(false);
            credits.Add(new ReleaseArtistCredit { ArtistId = artistId, CreditName = credit.CreditName, Position = credit.Position });
        }

        var links = new List<ReleaseLabelLink>();
        foreach (var link in spec.LabelLinks)
        {
            var labelId = await ResolveLabelIdAsync(link.LabelId, link.LabelRef, uow, cancellationToken).ConfigureAwait(false);
            links.Add(new ReleaseLabelLink { LabelId = labelId, IsPrimary = link.IsPrimary, Role = link.Role });
        }

        if (spec.Recordings.Count > 0)
            warnings.Add("release.recordings currently not persisted from release apply path.");

        return new Release
        {
            Id = id,
            Name = spec.Name,
            NormalizedName = spec.NormalizedName ?? EntityNameNormalizer.NormalizeStrict(FirstNonEmpty(spec.Title, spec.Name)),
            Title = spec.Title,
            SourcePayloadJson = spec.SourcePayloadJson,
            CreatedUtc = spec.CreatedUtc,
            UpdatedUtc = spec.UpdatedUtc ?? DateTimeOffset.UtcNow,
            ArtistCredits = credits,
            LabelLinks = links,
            ExternalReferences = spec.ExternalRefs.Select(r => new EntityReference
            {
                Source = r.Source,
                ExternalId = r.ExternalId,
                IsPrimary = r.IsPrimary,
                LastSeenUtc = r.LastSeenUtc,
                PayloadJson = r.PayloadJson,
            }).ToArray(),
        };
    }

    private static async Task<Recording> BuildRecordingModelAsync(
        string id,
        EntitySpec spec,
        IUnitOfWork uow,
        CancellationToken cancellationToken)
    {
        var credits = new List<RecordingArtistCredit>();
        foreach (var credit in spec.ArtistCredits)
        {
            var artistId = await ResolveArtistIdAsync(credit.ArtistId, credit.ArtistRef, uow, cancellationToken).ConfigureAwait(false);
            credits.Add(new RecordingArtistCredit
            {
                ArtistId = artistId,
                CreditName = credit.CreditName,
                Role = credit.Role,
                Position = credit.Position,
            });
        }

        var releaseLinks = new List<RecordingReleaseLink>();
        foreach (var link in spec.ReleaseLinks)
        {
            var releaseId = await ResolveReleaseIdAsync(link.ReleaseId, link.ReleaseRef, uow, cancellationToken).ConfigureAwait(false);
            releaseLinks.Add(new RecordingReleaseLink
            {
                ReleaseId = releaseId,
                DiscNumber = link.DiscNumber,
                TrackNumber = link.TrackNumber,
            });
        }

        var relationships = new List<RecordingRelationship>();
        foreach (var rel in spec.Relationships)
        {
            var relatedId = await ResolveRecordingIdAsync(rel.RelatedRecordingId, rel.RelatedRecordingRef, uow, cancellationToken).ConfigureAwait(false);
            relationships.Add(new RecordingRelationship
            {
                RelatedRecordingId = relatedId,
                RelationshipType = rel.RelationshipType,
                Source = rel.Source,
                Confidence = rel.Confidence,
                Notes = rel.Notes,
                CreatedUtc = rel.CreatedUtc,
                UpdatedUtc = rel.UpdatedUtc,
            });
        }

        return new Recording
        {
            Id = id,
            Name = spec.Name,
            NormalizedName = spec.NormalizedName ?? EntityNameNormalizer.NormalizeStrict(FirstNonEmpty(spec.Title, spec.Name)),
            Title = spec.Title,
            MixName = spec.MixName,
            Isrc = spec.Isrc,
            SourcePayloadJson = spec.SourcePayloadJson,
            CreatedUtc = spec.CreatedUtc,
            UpdatedUtc = spec.UpdatedUtc ?? DateTimeOffset.UtcNow,
            ArtistCredits = credits,
            ReleaseLinks = releaseLinks,
            Relationships = relationships,
            ExternalReferences = spec.ExternalRefs.Select(r => new EntityReference
            {
                Source = r.Source,
                ExternalId = r.ExternalId,
                IsPrimary = r.IsPrimary,
                LastSeenUtc = r.LastSeenUtc,
                PayloadJson = r.PayloadJson,
            }).ToArray(),
        };
    }

    private static async Task<string?> ResolveExistingIdAsync(string kind, EntitySpec spec, IUnitOfWork uow, CancellationToken cancellationToken)
    {
        var explicitId = FirstNonEmpty(spec.Id);
        if (!string.IsNullOrWhiteSpace(explicitId))
            return explicitId;

        foreach (var externalRef in spec.ExternalRefs)
        {
            if (string.IsNullOrWhiteSpace(externalRef.Source) || string.IsNullOrWhiteSpace(externalRef.ExternalId))
                continue;

            switch (kind)
            {
                case "Label":
                    {
                        var existing = await uow.Labels.GetByExternalRefAsync(externalRef.Source, externalRef.ExternalId, cancellationToken).ConfigureAwait(false);
                        if (existing is not null) return existing.Id;
                        break;
                    }
                case "Artist":
                    {
                        var existing = await uow.Artists.GetByExternalRefAsync(externalRef.Source, externalRef.ExternalId, cancellationToken).ConfigureAwait(false);
                        if (existing is not null) return existing.Id;
                        break;
                    }
                case "Release":
                    {
                        var existing = await uow.Releases.GetByExternalRefAsync(externalRef.Source, externalRef.ExternalId, cancellationToken).ConfigureAwait(false);
                        if (existing is not null) return existing.Id;
                        break;
                    }
                case "Recording":
                    {
                        var existing = await uow.Recordings.GetByExternalRefAsync(externalRef.Source, externalRef.ExternalId, cancellationToken).ConfigureAwait(false);
                        if (existing is not null) return existing.Id;
                        break;
                    }
            }
        }

        switch (kind)
        {
            case "Label":
                {
                    var normalized = spec.NormalizedName ?? EntityNameNormalizer.NormalizeStrict(spec.Name);
                    if (!string.IsNullOrWhiteSpace(normalized))
                    {
                        var existing = await uow.Labels.GetByNormalizedNameAsync(normalized, cancellationToken).ConfigureAwait(false);
                        return existing?.Id;
                    }
                    break;
                }
            case "Artist":
                {
                    var normalized = spec.NormalizedName ?? EntityNameNormalizer.NormalizeStrict(spec.Name);
                    if (!string.IsNullOrWhiteSpace(normalized))
                    {
                        var existing = await uow.Artists.GetByNormalizedNameAsync(normalized, cancellationToken).ConfigureAwait(false);
                        return existing?.Id;
                    }
                    break;
                }
            case "Release":
                {
                    var normalized = spec.NormalizedName ?? EntityNameNormalizer.NormalizeStrict(FirstNonEmpty(spec.Title, spec.Name));
                    if (!string.IsNullOrWhiteSpace(normalized))
                    {
                        var existing = await uow.Releases.GetByNormalizedTitleAndLabelAsync(normalized, null, cancellationToken).ConfigureAwait(false);
                        return existing?.Id;
                    }
                    break;
                }
            case "Recording":
                {
                    var normalized = spec.NormalizedName ?? EntityNameNormalizer.NormalizeStrict(FirstNonEmpty(spec.Title, spec.Name));
                    var mix = string.IsNullOrWhiteSpace(spec.MixName) ? null : EntityNameNormalizer.NormalizeStrict(spec.MixName);
                    if (!string.IsNullOrWhiteSpace(normalized))
                    {
                        var existing = await uow.Recordings.GetByNormalizedTitleAndMixNameAsync(normalized, mix, cancellationToken).ConfigureAwait(false);
                        if (existing is not null)
                            return existing.Id;
                    }

                    if (!string.IsNullOrWhiteSpace(spec.Isrc))
                    {
                        var existingIsrc = await uow.Recordings.GetByIsrcAsync(spec.Isrc!, cancellationToken).ConfigureAwait(false);
                        return existingIsrc?.Id;
                    }
                    break;
                }
        }

        return null;
    }

    private static async Task<string> ResolveArtistIdAsync(string? artistId, EntityRefSelector? artistRef, IUnitOfWork uow, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(artistId))
            return artistId;

        var byName = artistRef?.ByName;
        if (!string.IsNullOrWhiteSpace(byName))
        {
            var normalized = EntityNameNormalizer.NormalizeStrict(byName);
            var existing = await uow.Artists.GetByNormalizedNameAsync(normalized, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
                return existing.Id;
        }

        throw new ArgumentException("Unable to resolve artist reference. Provide artistId or artistRef.byName for an existing artist.");
    }

    private static async Task<string> ResolveLabelIdAsync(string? labelId, EntityRefSelector? labelRef, IUnitOfWork uow, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(labelId))
            return labelId;

        var byName = labelRef?.ByName;
        if (!string.IsNullOrWhiteSpace(byName))
        {
            var normalized = EntityNameNormalizer.NormalizeStrict(byName);
            var existing = await uow.Labels.GetByNormalizedNameAsync(normalized, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
                return existing.Id;
        }

        throw new ArgumentException("Unable to resolve label reference. Provide labelId or labelRef.byName for an existing label.");
    }

    private static async Task<string> ResolveReleaseIdAsync(string? releaseId, EntityRefSelector? releaseRef, IUnitOfWork uow, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(releaseId))
            return releaseId;

        var byTitle = releaseRef?.ByTitle;
        if (!string.IsNullOrWhiteSpace(byTitle))
        {
            var normalized = EntityNameNormalizer.NormalizeStrict(byTitle);
            var existing = await uow.Releases.GetByNormalizedTitleAndLabelAsync(normalized, null, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
                return existing.Id;
        }

        throw new ArgumentException("Unable to resolve release reference. Provide releaseId or releaseRef.byTitle for an existing release.");
    }

    private static async Task<string> ResolveRecordingIdAsync(string? recordingId, EntityRefSelector? recordingRef, IUnitOfWork uow, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(recordingId))
            return recordingId;

        var byTitle = recordingRef?.ByTitle;
        if (!string.IsNullOrWhiteSpace(byTitle))
        {
            var normalized = EntityNameNormalizer.NormalizeStrict(byTitle);
            var existing = await uow.Recordings.GetByNormalizedTitleAndMixNameAsync(normalized, null, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
                return existing.Id;
        }

        throw new ArgumentException("Unable to resolve recording reference. Provide relatedRecordingId or relatedRecordingRef.byTitle for an existing recording.");
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}

public sealed class EntityEnvelope
{
    public string ApiVersion { get; set; } = "catalog.trackstash/v1";
    public string Kind { get; set; } = string.Empty;
    public string Mode { get; set; } = "replace";
    public EntityMetadata? Metadata { get; set; }
    public EntitySpec Spec { get; set; } = new();
}

public sealed class EntityMetadata
{
    public string? Id { get; set; }
}

public sealed class EntitySpec
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? NormalizedName { get; set; }
    public string? SortName { get; set; }
    public string? Title { get; set; }
    public string? MixName { get; set; }
    public string? Isrc { get; set; }
    public string? SourcePayloadJson { get; set; }
    public DateTimeOffset? CreatedUtc { get; set; }
    public DateTimeOffset? UpdatedUtc { get; set; }
    public List<EntityAliasSpec> Aliases { get; set; } = [];
    public List<EntityRefSpec> ExternalRefs { get; set; } = [];
    public List<ArtistCreditSpec> ArtistCredits { get; set; } = [];
    public List<LabelLinkSpec> LabelLinks { get; set; } = [];
    public List<RecordingLinkSpec> Recordings { get; set; } = [];
    public List<ReleaseLinkSpec> ReleaseLinks { get; set; } = [];
    public List<RelationshipSpec> Relationships { get; set; } = [];
}

public sealed class EntityAliasSpec
{
    public string Value { get; set; } = string.Empty;
    public string? NormalizedValue { get; set; }
    public bool IsPrimary { get; set; }
}

public sealed class EntityRefSpec
{
    public string Source { get; set; } = string.Empty;
    public string ExternalId { get; set; } = string.Empty;
    public bool IsPrimary { get; set; }
    public DateTimeOffset? LastSeenUtc { get; set; }
    public string? PayloadJson { get; set; }
}

public sealed class ArtistCreditSpec
{
    public string? ArtistId { get; set; }
    public EntityRefSelector? ArtistRef { get; set; }
    public string? CreditName { get; set; }
    public string? Role { get; set; }
    public int? Position { get; set; }
}

public sealed class LabelLinkSpec
{
    public string? LabelId { get; set; }
    public EntityRefSelector? LabelRef { get; set; }
    public bool IsPrimary { get; set; }
    public string? Role { get; set; }
}

public sealed class RecordingLinkSpec
{
    public string? RecordingId { get; set; }
    public EntityRefSelector? RecordingRef { get; set; }
    public int? DiscNumber { get; set; }
    public int? TrackNumber { get; set; }
}

public sealed class ReleaseLinkSpec
{
    public string? ReleaseId { get; set; }
    public EntityRefSelector? ReleaseRef { get; set; }
    public int? DiscNumber { get; set; }
    public int? TrackNumber { get; set; }
}

public sealed class RelationshipSpec
{
    public string? RelatedRecordingId { get; set; }
    public EntityRefSelector? RelatedRecordingRef { get; set; }
    public string RelationshipType { get; set; } = string.Empty;
    public string? Source { get; set; }
    public decimal? Confidence { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset? CreatedUtc { get; set; }
    public DateTimeOffset? UpdatedUtc { get; set; }
}

public sealed class EntityRefSelector
{
    public string? ByName { get; set; }
    public string? ByTitle { get; set; }
}
