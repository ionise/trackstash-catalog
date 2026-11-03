# Catalog API Design Intent

Status: planning only; no HTTP host or API client is implemented by this document.

## Purpose and Scope

Provide a provider-independent HTTP interface to Trackstash's canonical music catalog. Start with labels, then expand without changing identity semantics or requiring clients to know the database backend.

Primary clients:

- React-admin web application for interactive maintenance and review.
- PowerShell HTTP client for interactive CLI access, automation, and pipelines.
- Future integrations using the same documented API.

Borrow useful music-resource and discovery conventions from Beatport, not its proprietary contract or provider-specific identity assumptions. Trackstash GUIDs remain canonical; Beatport and other source IDs remain external references.

Catalog includes music not owned locally. File scanning, admission, copying, backup, transcoding, and tag writeback remain Library responsibilities and are not introduced as catalog CRUD operations.

## Placement and Ownership

Keep the initial host inside this module:

```text
src/
  TrackStash.Catalog/               Existing CLI
  TrackStash.Catalog.Application/   Proposed shared catalog use cases
  TrackStash.Catalog.Api/           Proposed ASP.NET Core HTTP host
TrackStash.Catalog.Client/          Proposed PowerShell HTTP client
```

Extract shared application workflows incrementally; do not restructure unrelated code just to add a host.

```text
React-admin -> HTTP API -----------+
PowerShell client -> HTTP API ----+-> catalog application services
Existing local .NET CLI ----------+           |
                                  core domain/storage contracts
                                             |
                                  configured storage adapter
```

- Core owns shared models, normalization/identity rules, storage contracts, and adapters.
- Catalog application services own use cases and transaction orchestration.
- API owns routing, DTOs, authentication/authorization, HTTP concurrency, and error mapping.
- UI and PowerShell own presentation and transport adaptation, not matching or storage rules.
- CLI and API call shared services; neither launches the other.
- SQLite is a development adapter. No SQL, database paths, physical table names, or SQLite-specific semantics belong in public resource contracts.
- The server selects/configures its catalog; clients must not supply arbitrary database paths. Multi-catalog selection is a later explicit authorization/design decision.

## Label Resource

Use `/api/v1` as the initial route prefix. Proposed label representation:

```json
{
  "id": "564a50ecfffd44b1ace563a8321c5168",
  "name": "Mango Alley",
  "normalizedName": "mangoalley",
  "sortName": "Mango Alley",
  "createdUtc": "2026-11-03T06:00:00Z",
  "updatedUtc": "2026-11-03T06:00:00Z"
}
```

- `id` is assigned by the server, immutable, and independent of name/provider.
- Reuse the canonical GUID generator. Reconcile existing 32/36-character serialization differences before freezing the HTTP contract; initially propose the current helper's lowercase 32-character form for API output.
- `normalizedName` and timestamps are server-owned. Use `EntityNameNormalizer`, not client-specific normalization.
- Expose explicit response/request DTOs rather than serializing storage models directly.
- Name similarity is a review signal, not an automatic merge. Identical names may represent distinct provider identities.
- Aliases, external references, and relationships have dedicated resource operations; updating a name must not implicitly replace these collections.
- Arbitrary provider payloads should not be editable as ordinary label fields. Expose bounded provenance views separately when needed.

## Proposed Operations and Delivery Order

### First vertical slice

| Method | Route | Intent |
| --- | --- | --- |
| GET | `/labels` | Search/filter/sort/paginate labels |
| POST | `/labels` | Create label with server-assigned GUID |
| GET | `/labels/{id}` | Read label |
| PATCH | `/labels/{id}` | Update name/sort name |
| GET | `/labels/{id}/aliases` | Read aliases |
| GET | `/labels/{id}/external-references` | Inspect provider links |

The initial slice is complete when the same label can be listed, inspected, created, and edited through both the web UI and PowerShell, with persistent IDs and concurrency/error behavior tested.

### Subsequent label workflows

- Add/remove aliases, with original text retained and server-generated comparison keys.
- Maintain provider references with provenance and duplicate-link conflict checks.
- Add/read/update/remove label relationships.
- Preview deletion dependencies and delete through existing shared safe-delete/tombstone services.
- Review duplicate candidates, preview merges, and explicitly approve merges with retired-ID redirects.

Do not expose unsupported placeholder endpoints as successful operations. Exact child-resource routes and identifiers remain to be settled: current aliases/references use composite keys, and label relationships lack persistence.

### Relationship Semantics

- Alias: another name for the same canonical label GUID.
- Relationship: two distinct label GUIDs, not an excuse to merge identities.
- Proposed directional vocabulary: `parent_of` (parent -> child), `imprint_of` and `brand_of` (child -> parent), `distributed_by` (represented label -> distributor).
- Choose one stored representation for an equivalent hierarchy edge; derive inverse views rather than store redundant `parent_of`/child-parent assertions.
- Preserve source, confidence/review state, notes, territory, and effective dates where known.
- Reject self-links. Define cycle checks for hierarchical relationship types; distribution links are not automatically ownership hierarchy.
- Do not impose a universal single-parent rule without considering territory/time and joint ownership.
- Support a sublabel/parent view in the GUI using the same relationship API, not separate identities.

See core's [label schema](../../trackstash-core/docs/label-schema.md) for the existing design. Resolve directional naming and storage-contract details there before implementation.

## List and Search Contract

Proposed request:

```http
GET /api/v1/labels?q=univack&page=1&pageSize=25&sort=name&order=asc
```

Proposed response:

```json
{
  "items": [],
  "total": 0,
  "page": 1,
  "pageSize": 25
}
```

- Default page size 25; proposed maximum 100. Reject invalid pagination rather than silently reinterpret it.
- Return total matching count before pagination. Use deterministic sorting with ID as a tie-breaker; allowlist supported sort/filter fields.
- Initial search covers canonical names and aliases. Exact GUID/provider-reference lookup should use explicit filters, not depend on fuzzy search.
- Define comparison/search semantics at the service boundary so provider-specific collations do not silently change behavior.
- Keep ordinary search separate from similarity/duplicate-review ranking. Embeddings are optional candidate evidence, not identity authority.
- A React-admin data provider translates the public contract to `data`/`total`; do not bind public routes to framework-specific filter syntax.
- Extend filters additively. Consider cursor pagination later for large recording/file collections instead of assuming offset pagination is sufficient forever.

## Mutation, Concurrency, and Errors

- `POST` returns `201 Created`, `Location`, resource, and ETag. Reads return `200` and an opaque ETag.
- Proposed updates use JSON Merge Patch (`application/merge-patch+json`) with an explicit editable-field allowlist. Omitted values remain unchanged; null clears nullable fields; name cannot be cleared.
- Require `If-Match` for updates/deletes and related aggregate mutations: missing precondition -> `428`; stale version -> `412`. Define an adapter-neutral persisted version with atomic compare-and-write; timestamps alone are not sufficient.
- Alias/reference/relationship changes must invalidate the affected aggregate version consistently. Resolve cross-label version ownership before relationship editing ships.
- Use `400` for invalid requests, `404` for missing resources, `409` for domain conflicts/deletion blockers, `401`/`403` for access failures.
- Errors use standard Problem Details (`application/problem+json`) with stable domain error codes, field errors where applicable, and a trace ID. Do not expose SQL, credentials, or stack traces.
- Return canonical IDs and explicit conflict evidence; never silently merge or redirect a write to another identity.
- Preserve reviewed decisions and provenance. Define mutation audit storage before offering bulk edits or merges; do not imply existing timestamps constitute a full audit log.
- Child changes and domain mutations are transactional. A preview is advisory; recheck constraints/version on apply.
- Retries must not create duplicate labels: define create idempotency before enabling automatic POST retries or bulk import.

## Host and Client Requirements

- Begin with local-only loopback binding and explicit development configuration; loopback alone is not a complete authorization policy.
- Select authentication and authorization before editable deployment, particularly LAN/remote access. Allowlist web origins; address CSRF if cookie authentication is chosen.
- API host must not automatically migrate production databases on startup. Migrations remain an explicit bootstrap/deployment operation.
- Generate OpenAPI from the implementation; cover request/response shapes, pagination, Problem Details, and ETag headers with contract tests.
- Keep schema/API version evolution additive where possible; breaking HTTP changes require a version/migration policy.

### PowerShell HTTP Client

Keep it separate from the existing offline `TrackStash.Catalog.Artifacts` module:

- Proposed commands: `Connect-TrackStashCatalog`, `Get-TrackStashLabel`, `New-TrackStashLabel`, `Set-TrackStashLabel`, and subsequent alias/relationship commands.
- Return pipeline-friendly objects with IDs and concurrency metadata.
- Page explicitly by default and provide an explicit `-All` option.
- Mutations implement `SupportsShouldProcess`, `-WhatIf`, and `-Confirm`; `-WhatIf` sends no mutation. Detailed impact previews call read/preview endpoints.
- Surface structured API errors; do not convert failures into empty successful results.
- Keep credentials out of scripts/logs and avoid hidden global connection state. Allow explicit connection objects for multiple catalogs.

See the [PowerShell interface plan](powershell-interface/README.md).

## Expansion Path

| Resource | Intended direction |
| --- | --- |
| Artists | Canonical GUIDs, aliases, provider references, credits and appropriate relationships |
| Releases | Canonical GUIDs, label/artist links, release dates and recording placements |
| Recordings | Abstract track/master GUIDs; zero owned files required; many releases/representations; separate remasters |
| Genres/subgenres | Review existing taxonomy needs first; canonical terms, provider mappings, and explicit hierarchy rather than Beatport names as IDs |
| Playlists | Separate playlist identity and ordered entries; decide recording/release-placement targets, duplicate entries and versioning before designing persistence |
| Media files | Read-oriented links to Library inventory through an appropriate service; catalog does not acquire backup/scanning/tagging responsibilities |

Use `recordings` as the canonical abstract-track resource, not a second competing `tracks` identity. Future playlist export/playback can resolve catalog entries to available Library files; unavailable recordings remain valid catalog entries.

Hierarchical genre browsing, provider-style filters, charts, and discovery may draw inspiration from Beatport, but are deferred. Do not add a generic entity/relationship framework or premature tables for these future features.

## Implementation Plan and Acceptance Gates

1. Inventory reusable catalog/core contracts and gaps: paged search/count, safe updates, atomic versioning, alias/reference operations, relationship persistence, and diagnostics.
2. Freeze the label DTO, query, patch, identity-format, concurrency, error, and access contracts; document examples/OpenAPI expectations.
3. Extract only the shared application use cases needed; implement/test provider-independent services and SQLite adapter support.
4. Add `TrackStash.Catalog.Api` with local configuration, explicit storage selection, OpenAPI, and label endpoints. Test against a temporary catalog, not the live scanner database.
5. Add the minimal React-admin label list/detail/create/edit screens and PowerShell client against the same API.
6. Add label relationship persistence and workflows, then safe deletion and duplicate/merge review as separate increments.
7. Expand to other entities after the label contract has proven usable.

Tests must cover pagination totals/stable sorting, validation, normalization, immutable IDs, create/reapply identity behavior, stale edits, transactional rollback, missing resources, aliases/provider-reference ambiguity, safe deletion, and client error handling. Add relationship direction/cycle and merge-redirect tests with those capabilities. Verify UI and PowerShell behavior, not just repository methods.

Before implementation, settle the open choices above rather than assuming current migrations or services already provide them. Planning does not authorize changes to existing catalog data, media files, or running Library jobs.
