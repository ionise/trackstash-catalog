# TrackStash.Catalog.Artifacts

This module is the PowerShell interface for TrackStash catalog artifact authoring and discovery.

It is intended to cover:

- YAML artifact generation for labels, artists, releases, and recordings
- catalog commit/apply workflows
- catalog read/search workflows

The current state includes artifact authoring, publish/apply, and foundational read/search cmdlets.

## Layout

- `TrackStash.Catalog.Artifacts.psd1` module manifest
- `TrackStash.Catalog.Artifacts.psm1` module loader
- `public/` exported cmdlet stubs
- `private/` internal helper stubs
- `classes/` module types

Implemented so far:

- `New-TrackStashLabelYamlArtifact`
- `New-TrackStashArtistYamlArtifact`
- `New-TrackStashReleaseYamlArtifact`
- `New-TrackStashRecordingYamlArtifact`
- `New-TrackStashCatalogYamlArtifacts`
- `Publish-TrackStashCatalogArtifact`
- `Get-TrackStashCatalogEntity`
- `Find-TrackStashCatalogEntity`
- `Search-TrackStashCatalogEntity`
- `Get-TrackStashCatalogSummary`

Implemented private template/yaml helpers:

- `Get-TrackStashDefaultEntityTemplate`
- `Merge-TrackStashTemplateData`
- `ConvertTo-TrackStashYamlDocument`

## Notes

- Artifact generation should stay separate from catalog publish/apply.
- Read/search commands should stay read-only.
- Slug and normalized-name resolution should flow through catalog/core, not be duplicated in the module.

## Versioning

When staged changes touch the module's `public/`, `private/`, `classes/`, or `.psm1` files, the repo-local pre-commit hook increments the `ModuleVersion` build segment in `TrackStash.Catalog.Artifacts.psd1`.