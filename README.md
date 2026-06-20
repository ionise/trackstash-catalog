# trackstash-catalog

Canonical catalog and lifecycle operations module for TrackStash.

## Overview

`trackstash-catalog` owns the day-2 catalog workflows that happen after storage has been initialized.
It builds on shared contracts and services from `trackstash-core` and leaves first-run setup to `trackstash-bootstrap`.

This module is the long-term home for:

- canonical catalog ingestion
- catalog-oriented diagnostics
- lifecycle-safe repair and maintenance workflows
- read/query surfaces for other TrackStash modules

## Why This Module Exists

TrackStash needs a boundary between setup concerns and ongoing catalog work.
`trackstash-bootstrap` should remain small and focused on initialization, migrations, and starter orchestration.
`trackstash-catalog` should own the repeated workflows used during normal operation.

Examples:

- importing curated CSV batches into the canonical catalog
- ingesting Beatport or other external source data
- checking referential integrity and catalog health
- repairing derived indexes or denormalized search structures
- exposing catalog summaries to matching, tagging, and organization flows

## How It Fits In The Project

`trackstash-catalog` depends on shared domain contracts and reusable services from `trackstash-core`.
It should not introduce direct knowledge of SQLite internals into command handlers.

```mermaid
flowchart LR
    C[trackstash-catalog] --> K[trackstash-core contracts and services]
    C --> S[TrackStash.Core.Sqlite adapter]
    C --> P[psBeatPort future]

    K --> DB[(catalog database)]
    S --> DB
```

Related module docs:

- `../trackstash-core/README.md`
- `../trackstash-core/docs/ecosystem-modules.md`
- `../trackstash-bootstrap/README.md`

## Responsibilities

`trackstash-catalog` should own:

- operational catalog imports and refreshes
- canonical entity maintenance beyond first-run seeding
- catalog diagnostics such as integrity and completeness checks
- repair flows for indexes and derived search structures
- catalog-facing summaries and inspection commands
- reusable application-layer orchestration for future UI or service hosts

`trackstash-catalog` should not own:

- database bootstrap or migration initialization
- raw filesystem scanning
- audio fingerprint generation
- media-file writeback and tag mutation
- low-level repository and provider contract definitions

## Command Surface

Current commands:

- `import-csv`
- `summary`
- `doctor`
- `delete-entity`
- `repair-indexes`

Notes:

- `doctor` currently focuses on readiness and consistency heuristics using provider-agnostic contracts.
- `repair-indexes` currently provides an idempotent maintenance entry point and dry-run reporting; backend-specific rebuild actions can be registered as catalog adds derived index structures.

## Entity Templates (v1)

`trackstash-catalog` now includes full-schema desired-state YAML templates for canonical entities:

- `templates/entities/label.v1.yaml`
- `templates/entities/artist.v1.yaml`
- `templates/entities/release.v1.yaml`
- `templates/entities/recording.v1.yaml`
- `templates/entities/batch-example.v1.yaml`

These templates are intended to be a common interchange contract for CLI workflows, bash scripts, and PowerShell cmdlets.

See `docs/entity-templates/README.md` for:

- `validate-entity`, `apply-entity --dry-run`, and apply flow
- mode semantics (`replace`, `merge`, `create-only`, `update-only`)
- full-schema field coverage notes and export/import expectations

Likely later commands:

- `import-beatport`
- `rebuild-embeddings`
- `resolve-aliases`
- `show-entity`
- `find-duplicates`

## Ownership Clarification

Current state:

- `trackstash-bootstrap` may expose compatibility wrappers, while `trackstash-catalog` is the primary operational CLI for catalog workflows.
- `trackstash-core` owns reusable storage contracts and services used by catalog commands.
- delete semantics are implemented in shared storage contracts/services and surfaced through `trackstash-catalog delete-entity`.

Target state:

- `trackstash-catalog` becomes the primary CLI and application boundary for `import-csv` and future catalog imports.
- `trackstash-bootstrap` may keep a compatibility wrapper for first-run convenience, but should not become the long-term home for catalog lifecycle features.

## First Milestones

### Milestone 1: CLI and command host

- create the .NET solution and project layout
- add shared config and output conventions aligned with bootstrap
- wire the SQLite provider through core abstractions

### Milestone 2: Move lifecycle import here

- adopt `TrackStash.Core.Services.CatalogImportService`
- expose `import-csv` as the primary operational command
- preserve `--dry-run`, `--fail-fast`, warning counts, and row-level reporting

### Milestone 3: Catalog health and repair

- implement `summary` for quick catalog counts and readiness
- implement `doctor` for integrity diagnostics
- implement `delete-entity` with dependency-aware safety checks after core delete contracts and rules are finalized
- implement `repair-indexes` for derived index refresh and validation

### Planned delete rules

The first delete feature should be conservative and explicit.

- `label` cannot be deleted while any `release_label_link` rows still point to it
- `artist` cannot be deleted while any `release_artist_credit` or `recording_artist_credit` rows still point to it
- `release` cannot be deleted while any `release_recording` or `release_artist_credit` rows still point to it
- `recording` cannot be deleted while any `release_recording`, `recording_artist_credit`, `recording_relationship`, `media_file_recording_match`, or `media_file_recording_candidate` rows still point to it

Rows that are owned by the entity itself should usually be deleted automatically inside the same transaction rather than treated as blockers.
Examples include external references, aliases, and embedding documents.

### Milestone 4: Source ingestion and enrichment

- add external import surfaces such as Beatport ingestion
- support richer alias, relationship, and provenance maintenance
- prepare read models for matching and tagging modules

## Design Constraints

- depend on `trackstash-core` contracts and services instead of duplicating logic
- keep command handlers thin and orchestration-focused
- prefer idempotent operations wherever feasible
- preserve stable JSON output for automation
- keep failure modes explicit, especially for partial imports and repair flows

## Testing Strategy

The first implementation should mirror the testing style already used in neighboring repos:

- integration tests against temporary SQLite databases
- command-level tests for exit codes and output payloads
- focused tests around idempotency, warning emission, and repair safety

## Progress To Date

Implemented:

- CLI scaffold with shared config resolution and text/json output envelopes
- `import-csv`, `summary`, `doctor`, `delete-entity`, and `repair-indexes` command handlers
- provider-agnostic catalog command orchestration via `IStorageProviderFactory`
- integration coverage for import, summary, delete, and command-level CLI behavior

Still evolving:

- richer `doctor` checks for deeper orphan/relationship diagnostics as shared query contracts grow
- backend-specific `repair-indexes` actions for future derived index materializations
- additional provider registrations beyond sqlite

## Current Status

Status: active implementation with core operational commands in place.

`trackstash-catalog` is scaffolded, tested, and usable for import, summary, doctor diagnostics, delete, and repair entry-point workflows.
Ongoing work is focused on expanding diagnostics depth, repair actions, and additional storage provider support.
