# TrackStash PowerShell Interface

This document captures the proposed PowerShell interface for catalog authoring and catalog discovery workflows.

## Scope

This interface is intended to live with `trackstash-catalog` and should remain focused on canonical catalog data:

- Labels
- Artists
- Releases
- Recordings

It should not absorb scan/file-library responsibilities. That boundary is reserved for the eventual Library-side tooling.

## Module Placement

For now, the PowerShell module should be treated as part of the catalog project rather than a separate TrackStash module.

Recommended module name:

- `TrackStash.Catalog.Artifacts`

This name reflects the primary purpose of the interface: authoring, publishing, and querying catalog artifacts.

## Module Layout Pattern

The PowerShell module should follow this layout pattern:

- A module directory named the same as the module
- A `README.md` inside the module directory with module purpose and usage guidance
- A module manifest file `.psd1`
- A module file `.psm1`
- One `.ps1` file per function
- Public functions stored in `public/`
- Private helper functions stored in `private/`
- Classes stored in `classes/`

The `.psm1` file should load those directories in order and export only public functions.

Example structure:

```text
TrackStash.Catalog.Artifacts/
  README.md
  TrackStash.Catalog.Artifacts.psd1
  TrackStash.Catalog.Artifacts.psm1
  public/
    New-TrackStashLabelYamlArtifact.ps1
    New-TrackStashArtistYamlArtifact.ps1
    New-TrackStashReleaseYamlArtifact.ps1
    New-TrackStashRecordingYamlArtifact.ps1
    Publish-TrackStashCatalogArtifact.ps1
    Find-TrackStashCatalogEntity.ps1
    Get-TrackStashCatalogEntity.ps1
    Search-TrackStashCatalogEntity.ps1
  private/
    ConvertTo-TrackStashSlug.ps1
    ConvertTo-TrackStashYamlDocument.ps1
    Ensure-TrackStashArtifactDirectory.ps1
    Get-TrackStashDefaultEntityTemplate.ps1
    Merge-TrackStashTemplateData.ps1
    Resolve-TrackStashArtifactPath.ps1
    Write-TrackStashYamlFile.ps1
  classes/
    TrackStashCatalogEntityArtifact.ps1
```

## Design Principles

- Keep artifact generation separate from catalog commit/apply.
- Keep catalog discovery read-only.
- Keep commands small and composable so they can be used from scripts or interactive sessions.
- Keep YAML generation aligned with the catalog entity contract and existing CLI templates.
- Prefer deterministic file names and directories so repeated runs are stable.

## Artifact Generation Workflow

The artifact generator should create one YAML file per entity.

Recommended conventions:

- directory name = entity kind in lowercase (`label`, `artist`, `release`, `recording`)
- file name = slug of the entity plus `.yaml`
- file content = desired-state YAML envelope used by `trackstash-catalog`

Example output layout:

```text
artifacts/
  label/
    virelith-records.yaml
  artist/
    bozra-bozra.yaml
  release/
    virelith-sessions.yaml
  recording/
    signal-drift-original-mix.yaml
```

## Proposed Cmdlet Families

### Artifact Authoring Cmdlets

These cmdlets create YAML files on disk.

- `New-TrackStashLabelYamlArtifact`
- `New-TrackStashArtistYamlArtifact`
- `New-TrackStashReleaseYamlArtifact`
- `New-TrackStashRecordingYamlArtifact`
- `New-TrackStashCatalogYamlArtifacts` (batch helper)

Recommended behavior:

- accept either direct parameters or pipeline input
- infer output path from entity kind and slug
- create missing output directories automatically
- avoid overwriting unless explicitly instructed
- emit a structured object that reports the written path and slug

### Commit / Apply Cmdlet

This cmdlet commits YAML artifacts into the catalog.

- `Publish-TrackStashCatalogArtifact`

Recommended behavior:

- accept one file or a directory tree of YAML artifacts
- validate the YAML before applying it
- call the catalog apply logic rather than duplicating storage rules
- support `-WhatIf` / preview-style operation if feasible
- keep artifact creation and catalog commit separate

### Read / Search Cmdlets

These cmdlets are for discovery and inspection.

- `Get-TrackStashCatalogEntity`
- `Find-TrackStashCatalogEntity`
- `Search-TrackStashCatalogEntity`
- `Get-TrackStashCatalogSummary`

Recommended behavior:

- `Get-TrackStashCatalogEntity` should return one exact entity by ID, slug, or reference
- `Find-TrackStashCatalogEntity` should support filtered lookup by kind, name, normalized name, slug, label, ISRC, or reference
- `Search-TrackStashCatalogEntity` should support broader text search and discovery workflows
- `Get-TrackStashCatalogSummary` should provide a quick overview of catalog state

## Catalog vs Library Boundary

Use these terms consistently:

- Catalog: canonical music metadata, including music the user may not own locally
- Library: owned media-file metadata and file lifecycle state

This PowerShell interface belongs to Catalog scope. It should not become the owner of Library scanning or file inventory behavior.

## Relationship To Existing Catalog CLI

The PowerShell interface should be aligned with `trackstash-catalog` rather than inventing a separate schema or workflow.

That means:

- YAML artifact format should match the existing `template`, `validate-entity`, `apply-entity`, and `get-entity` commands
- generated files should be reusable by the catalog CLI
- read-only query cmdlets should mirror the same entity model used by the catalog CLI

## Recommended Implementation Order

1. Implement private helpers for slugging, paths, and YAML output.
2. Implement the label and artist artifact cmdlets.
3. Implement the release and recording artifact cmdlets.
4. Implement the commit/apply cmdlet.
5. Implement read/search cmdlets.
6. Add a batch helper once single-entity flows are stable.

## Open Design Notes

- Whether the commit cmdlet should accept a directory, explicit file list, or both.
- Whether search should return plain objects or formatted text by default.
- Whether the module should later gain Library-side cmdlets or remain Catalog-only.
- Whether `Publish-TrackStashCatalogArtifact` should be a wrapper around `trackstash-catalog apply-entity` or call shared core services directly.
