# TrackStash.Catalog.Artifacts

This module is the scaffold for the TrackStash catalog PowerShell interface.

It is intended to cover:

- YAML artifact generation for labels, artists, releases, and recordings
- catalog commit/apply workflows
- catalog read/search workflows

The current state is a module scaffold. Public cmdlet files, private helper files, and the module loader are in place so implementation can be filled in incrementally.

## Layout

- `TrackStash.Catalog.Artifacts.psd1` module manifest
- `TrackStash.Catalog.Artifacts.psm1` module loader
- `public/` exported cmdlet stubs
- `private/` internal helper stubs
- `classes/` module types

## Notes

- Artifact generation should stay separate from catalog publish/apply.
- Read/search commands should stay read-only.
- Slug and normalized-name resolution should flow through catalog/core, not be duplicated in the module.

## Versioning

When staged changes touch the module's `public/`, `private/`, `classes/`, or `.psm1` files, the repo-local pre-commit hook increments the `ModuleVersion` build segment in `TrackStash.Catalog.Artifacts.psd1`.