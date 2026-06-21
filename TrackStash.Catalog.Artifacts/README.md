# TrackStash.Catalog.Artifacts

This module is the PowerShell interface for TrackStash catalog artifact authoring and discovery.

It is intended to cover:

- YAML artifact generation for labels, artists, releases, and recordings
- catalog commit/apply workflows
- catalog read/search workflows

The current state includes the first implemented artifact authoring and publish/apply cmdlets, with the remaining read/search surface still to be built out incrementally.

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

## Notes

- Artifact generation should stay separate from catalog publish/apply.
- Read/search commands should stay read-only.
- Slug and normalized-name resolution should flow through catalog/core, not be duplicated in the module.

## Versioning

When staged changes touch the module's `public/`, `private/`, `classes/`, or `.psm1` files, the repo-local pre-commit hook increments the `ModuleVersion` build segment in `TrackStash.Catalog.Artifacts.psd1`.