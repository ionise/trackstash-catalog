# Catalog API Design Intent

Status: shared label foundation, reviewed pairwise label merges, loopback-only HTTP host, and React-admin label GUI implemented with OpenAPI, integration tests, and browser tests. PowerShell HTTP client remains planned.

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
  TrackStash.Catalog.Application/   Shared label use cases
  TrackStash.Catalog.Api/           ASP.NET Core HTTP host
  TrackStash.Catalog.Admin/         React-admin label GUI (Vite/TypeScript)
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
  "sortName": "Mango Alley",
  "version": "opaque-storage-version",
  "createdUtc": "2026-11-03T06:00:00Z",
  "updatedUtc": "2026-11-03T06:00:00Z",
  "aliases": [],
  "externalReferences": []
}
```

- `id` is assigned by the server, immutable, and independent of name/provider.
- New IDs use the canonical generator's lowercase 32-character GUID form. Existing IDs (including legacy strings or hyphenated GUIDs) are addressed and returned verbatim, not rewritten.
- Normalization and timestamps are server-owned. Use `EntityNameNormalizer`, not client-specific normalization. Normalized names are query filters, not editable fields or detail-response properties.
- Expose explicit response/request DTOs rather than serializing storage models directly.
- Name similarity is a review signal, not an automatic merge. Identical names may represent distinct provider identities.
- Aliases, external references, and relationships have dedicated resource operations; updating a name must not implicitly replace these collections.
- Arbitrary provider payloads should not be editable as ordinary label fields. Expose bounded provenance views separately when needed.

## Proposed Operations and Delivery Order

### First vertical slice (HTTP and GUI implemented; PowerShell pending)

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
- Reviewed pairwise merges with retired-ID redirects are implemented below; automatic/bulk deduplication remains excluded.

Do not expose unsupported placeholder endpoints as successful operations. Exact child-resource routes and identifiers remain to be settled: current aliases/references use composite keys, and label relationships lack persistence.

### Reviewed Label Merge Design

Status: **first increment implemented** through core contracts, SQLite migration 4, catalog application services, authenticated preview/apply routes, and a React-admin review dialog. Migration and live merges remain separate operator actions; automated tests use temporary catalogs. This section records the agreed design and concrete initial policy.

#### Identity and Scope

A canonical entity can have multiple references to the same provider. Current `label_external_ref` has primary key `(label_id, source, external_id)`, so combining two Beatport IDs on one label does **not** require additional foreign-key columns. Artist, release, and recording external-reference tables use the same pattern. New persistence is needed for retirement, audit, and reviewed operations, not for multiple provider IDs.

Start with a **pairwise, explicitly reviewed label merge**. The operator chooses a survivor and a label to retire. Keep the survivor's ID, name, sort name, and creation time; preserve the retired name as an alias. Choosing a different display name is a separate conditional edit, followed by a fresh preview. Do not guess equivalence from matching names, normalization, embeddings, or provider names.

For Univack, a preview can combine the existing labels' known Beatport references. A third provider identity must be independently verified and attached through a future reviewed reference workflow; merge must not invent or fetch an unconfirmed third ID.

This increment excludes automatic deduplication, multi-selection/bulk merges, undo/unmerge, provider-reference editing, artist/release/recording merge endpoints, and media tag/file changes. It may precede label relationship editing, but must reject dependencies it cannot safely transfer.

#### Preview and Confirmation

1. From label detail, choose **Merge duplicate into this label** and select the other active label through paged name/provider search. Show both IDs prominently; allow switching the survivor by requesting a new preview.
2. Display survivor fields, retired fields, combined aliases/provider IDs, release links to move/coalesce, warnings, blockers, and the proposed retired-ID redirect. Show same-provider references as a list, never a single-ID field.
3. Require a nonblank reason and explicit confirmation of the survivor/retired direction. The initial local bearer key identifies a shared local credential, not an individual human: audit should record a local credential principal, plus optional operator-supplied attribution marked as unverified. Never store the key.
4. A preview does not mutate catalog identities/links. It persists an immutable review with a 15-minute expiry, a server-assigned operation ID, input IDs/versions, dependency digest, deterministic public plan, and a plan format version incorporated into the state digest. Preview storage must not copy secrets into public responses.
5. Apply accepts that operation ID and the reason, not a client-supplied arbitrary merge plan. Check both versions and recompute the dependency digest and plan inside the write transaction. A changed or expired preview requires a new review, not automatic token refresh.
6. On success, navigate to survivor detail, invalidate both label caches and list totals, and show the retained provider IDs. On stale/conflict failure, preserve the reason and show that no merge occurred; do not silently retry.

Persisting the operation identity also gives apply safe retry semantics: a repeated request for an already-applied operation returns the recorded outcome without moving rows or writing another audit record. It does not reapply the merge even if the survivor has since changed. Return the original operation outcome and its audit ID, not a stale representation advertised as current label detail; clients fetch the survivor afresh.

#### Deterministic Transfer Rules

| Data | Proposed behavior |
| --- | --- |
| Survivor scalar fields | Keep existing values; recompute normalization from survivor name and refresh aggregate version/update time |
| Retired scalar fields and payload | Preserve in full before-state audit snapshot, not by overwriting survivor payload |
| Names and aliases | Add retired canonical name as a non-primary alias if not already present; union aliases by exact value (the current key), retain differently spelled variants, and recompute comparison keys with the shared normalizer |
| Alias primary flags | Survivor's explicit primary choice wins; transferred aliases are non-primary. Existing multiple-primary anomalies block until reviewed |
| Distinct provider references | Move every `(source, external_id)` to survivor, retaining last-seen time and payload. Multiple IDs from Beatport are valid |
| Duplicate provider reference | Coalesce identical keys; keep latest non-null last-seen observation. Identical/non-conflicting payloads coalesce; two different non-null payloads block the first increment rather than discard evidence |
| Reference primary flags | Interpret as preferred link per provider, not a uniqueness constraint on provider membership. Retain the survivor's choice; if none exists, retain a single retired primary. Multiple ambiguous primaries block |
| Release links | Move links referencing retired ID. If both labels link the same release, combine `is_primary` with OR; keep equal roles or the sole non-null role. Different non-null roles block because the current composite key cannot retain both |
| Existing redirects into either participant | Resolve them to the new survivor, preserving original retirement/audit history and preventing cycles |
| Derived label embeddings/work items | Invalidate/delete or supersede retired derived entries and invalidate survivor embeddings for regeneration through the existing queue contracts. Do not relabel old vectors as newly computed evidence |
| Unsupported dependencies | Block with public domain names/counts; never cascade-delete unknown relationships, images, or links |

Same `(source, external_id)` attached to a **third active label** is an identity conflict: block and present the third label for review. Different external IDs from the same provider do not conflict. Do not silently introduce a global unique-provider constraint during migration while unresolved duplicates may exist.

Every coalescing decision and original row must remain recoverable in the audit snapshot, including alias flags, payload observations, release roles/flags, original timestamps, and derived-data disposition. Audit preservation is not an implemented undo capability.

#### Persistence and Provider Boundary

Label-specific core contracts expose identity resolution (active/retired/missing), merge preview/apply results, and adapter operations to inspect dependencies and apply a plan transactionally. Catalog owns orchestration/review policy and transport projection. SQL, physical table names, serialization of audit rows, and lock strategy stay in storage adapters. Do not add a generic merge engine until another entity's rules justify it.

Explicit SQLite migration 4 adds:

- A retired-label mapping keyed by retired ID, pointing to an active survivor, with retirement time and merge audit ID. Retired IDs cannot also be active label IDs. Enforce this for ordinary inserts/upserts and restore as well as merge.
- A durable review/operation record with expiry, input IDs and versions, dependency digest, plan format version, status, and applied audit/outcome reference. Enforce single apply and preserve applied outcomes for retry; expired unused previews may be cleaned up separately.
- Append-only merge audit records with merge ID, participant IDs, UTC time, reason, principal/attribution, full relational before-state snapshots, after-state/transfer decisions, and affected release identities. Preserve provider payloads in restricted storage, not general HTTP responses.
- Adapter mechanisms to invalidate label versions when merge-relevant release links change, or equivalent dependency compare-and-lock support. Existing label/alias/reference version triggers alone do **not** cover release-link changes.

Snapshots must be built from actual relational state, not the existing deletion service's `source_payload_json` tombstone shortcut. Do not route merge through ordinary delete/restore. The source label is retired only after all supported dependencies are transferred; any failure rolls back identity rows, links, derived-data changes, redirects, operation status, and audit together.

Migration 4 preserves existing canonical IDs and references and never automatically merges a same-name group. The API refuses schemas below version 4 without migrating them. Back up and migrate the live canonical catalog only with separate approval. Scanner storage is out of scope.

SQLite uses immediate unit-of-work transactions to exclude concurrent writers throughout inspect/apply, plus a dependency digest and apply savepoint. Raw before-state snapshots include both labels, aliases, references, release links, incoming redirects, embedding documents/vectors, and work items. Unknown incoming foreign-key dependencies with rows block apply; processing embedding jobs also block to avoid racing a leased worker. Known label embedding models are queued for forced survivor regeneration after invalidation.

Unused/expired reviews and applied operation outcomes are retained in this increment; no cleanup job is installed. Audits and retired IDs are permanent and have database delete/update guards. Full audit snapshots are restricted to local database administration, not exposed by HTTP; protect database/backup access accordingly. There is no separate per-user audit permission model while using the shared local key.

#### Concurrency and Retired IDs

Apply must validate the survivor and retired label versions **and** the complete affected dependency set under a transaction/locking strategy that excludes conflicting writers until commit. A digest check outside the write transaction is insufficient. Version equality alone is also insufficient: release-link changes and newly introduced third-party provider conflicts must be rechecked. Include incoming redirects and derived work in the inspected dependency plan where relevant.

Retired IDs remain resolvable indefinitely. Proposed read behavior:

- Exact GET for a retired label (and its child inspection routes) returns `308 Permanent Redirect` to the corresponding survivor route. Collapse chains to an active target; audit history retains intermediate merges.
- List/search returns active identities only; retained aliases and provider IDs make the survivor discoverable.
- PATCH, delete, merge selection, or restore addressed to a retired ID returns `409 label_retired` with `canonicalId` and a survivor URI. Writes must never follow a redirect implicitly.
- Application/storage identity resolution returns retirement information explicitly; callers choose read resolution or write rejection. API handlers do not infer it from missing rows.

Prevent resurrection across **all** existing writers before shipping:

- Provider-ID imports resolve transferred references to the survivor.
- Explicit retired IDs in YAML/import artifacts or release-link selectors produce an actionable conflict; do not silently edit the survivor under an old identity or recreate a label.
- Low-level create/upsert and restore must reject retired-ID reuse, including legacy callers. Release writers must reject or explicitly resolve retired links; they cannot insert a fresh reference to a nonexistent active row.
- Same-name creation remains allowed because name equality is not identity. Merge does not authorize name-based deduplication of future records.
- Keep any survivor with incoming retired IDs from ordinary deletion until a lifecycle policy preserves resolution. Existing deletion commands must enforce this, not just HTTP routes.

If that survivor is later merged again, retarget incoming mappings atomically to the new active survivor. Reject self-merge, retired participants, cycles, and concurrent/reversed merges. No mutation should revive an intermediate ID.

#### Implemented Merge HTTP Surface

All routes retain existing bearer/origin protection and Problem Details conventions:

| Method and route | Contract |
| --- | --- |
| `POST /api/v1/labels/{survivorId}/merge-previews` | JSON `{ "retiredLabelId": "..." }`; 201 with Location and review DTO containing both label summaries, expected versions, expiry, impact, warnings/blockers, and review ETag |
| `GET /api/v1/label-merge-previews/{operationId}` | 200 review DTO with ETag; no raw snapshots/provider payloads |
| `POST /api/v1/label-merge-previews/{operationId}/apply` | JSON `{ "reason": "Confirmed same label", "operator": "optional unverified attribution" }` and `If-Match` review ETag; 200 outcome with operation/audit IDs and canonical survivor URI |

`If-Match` here protects the immutable reviewed plan; apply also validates live participant versions/dependencies. Missing precondition -> 428; stale tag or changed reviewed state -> 412; expired preview -> 410; missing resource -> 404; invalid body/self-merge -> 400; blocked plan/retired participant/conflicting replay -> 409. Add documented codes such as `merge_preview_expired`, `merge_state_changed`, `merge_blocked`, and `label_retired`.

An identical retry after success returns the saved outcome even though versions necessarily changed during merge, including after review expiry. A replay with a different reason/operator/principal returns 409; a mismatched review ETag returns 412. Distinguish an unapplied stale preview from a completed operation before validating current participant state. Expose operation outcome/audit metadata without exposing full audit snapshots; read DTOs must not leak payloads or credentials. GET of an expired, unapplied review returns 410; an applied review remains readable.

The GUI's label-detail action offers paged name/provider candidate search, preview, switch-survivor, reason, optional unverified operator attribution, and explicit direction confirmation. Refresh/switch requires confirmation again. Cancel never changes label data, and stale apply does not silently retry. Local YAML/CSV writes and dry runs reject explicit retired label IDs, including release-link selectors; transferred provider references resolve to the survivor. Local YAML `get-entity` reports the retired identity and canonical ID explicitly rather than exporting a misleading missing entity. No new merge CLI command is included.

#### Acceptance Gates and Expansion

Implemented tests cover the following acceptance gates:

- Same-name labels with distinct Beatport IDs merge into one active GUID retaining all known provider IDs; unrelated same-name labels remain separate without explicit selection.
- Alias spelling/primary rules, payload conflicts, provider primary choices, third-party reference conflicts, and release-link coalescing/blockers match the preview.
- Changes to either participant, release links, redirects, or relevant third-party references invalidate/reject the reviewed plan; competing applies cannot both succeed.
- Failure injection rolls back every transfer/audit/redirect and leaves both labels usable. Double submission/response loss replays the one saved result.
- Retired reads resolve, retired writes/restore/import/upsert cannot resurrect IDs, chained merges remain acyclic, and survivor deletion is blocked where necessary.
- Full snapshots contain original relational data even when provider payload is null or unrelated to the stored entity.
- Migration is data-preserving/idempotent; storage, application, HTTP/OpenAPI, existing CLI/import/delete, and browser preview/confirm/cancel/error tests pass against temporary catalogs.

Artists, releases, and recordings should reuse the identity/reference/retirement principles, not blindly reuse label transfer rules. Artist credits/relationships, release placements/dates, and recording media associations/matches/fingerprints require separate dependency and conflict policies. Multiple Beatport track IDs can identify one recording; remasters remain separate recordings under the agreed policy.

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

Implemented request:

```http
GET /api/v1/labels?q=univack&page=1&pageSize=25&sort=name&order=asc
```

Implemented response:

```json
{
  "items": [],
  "total": 0,
  "page": 1,
  "pageSize": 25
}
```

- Default page size 25; maximum 100. Reject invalid pagination rather than silently reinterpret it.
- Return total matching count before pagination. Use deterministic sorting with ID as a tie-breaker; allowlist supported sort/filter fields.
- Initial search covers canonical names and aliases. Exact GUID/provider-reference lookup should use explicit filters, not depend on fuzzy search.
- Define comparison/search semantics at the service boundary so provider-specific collations do not silently change behavior.
- Keep ordinary search separate from similarity/duplicate-review ranking. Embeddings are optional candidate evidence, not identity authority.
- A React-admin data provider translates the public contract to `data`/`total`; do not bind public routes to framework-specific filter syntax.
- Extend filters additively. Consider cursor pagination later for large recording/file collections instead of assuming offset pagination is sufficient forever.

## Mutation, Concurrency, and Errors

- `POST` returns `201 Created`, `Location`, resource, and ETag. Reads return `200` and an opaque ETag.
- Updates use JSON Merge Patch (`application/merge-patch+json`) with an explicit editable-field allowlist. Omitted values remain unchanged; null clears nullable fields; name cannot be cleared.
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

### Implemented Local HTTP Contract

[The API host](../src/TrackStash.Catalog.Api/CatalogApiHost.cs) uses .NET 8 minimal endpoints and the shared `ILabelService`. Only the composition root selects the SQLite adapter; handlers and request/response types do not access SQL or accept database paths. [Startup instructions](../README.md#running-the-api-locally) describe environment/user-secret configuration.

- The six first-slice routes above are implemented under `/api/v1`. Child collections are read-only arrays. Single-label detail/create/update returns `ETag`; POST also returns a relative `Location`. All responses use `Cache-Control: no-store`; conditional GET is not implemented.
- List query parameters are exactly `q`, `page`, `pageSize`, `sort`, `order`, `normalizedName`, `source`, and `externalId`. Unknown/repeated parameters and invalid values return 400. Sorting and matching follow the shared foundation below.
- Request property names are case-sensitive. Create accepts only `name` and optional `sortName`; patch accepts the same allowlist and rejects empty patches. Unknown, duplicate, or wrongly typed properties are rejected. Bodies are JSON objects limited to 16 KiB. JSON uses standard numeric typing, not string-to-number coercion.
- `ETag` is the quoted Base64 encoding of the UTF-8 opaque storage version. Clients must treat the whole header as opaque. PATCH requires exactly one strong quoted `If-Match`; weak tags, wildcards, lists, and malformed/noncanonical encodings return 400. A valid but outdated token returns 412; a missing precondition returns 428. A missing resource with a valid precondition returns 404.
- Error responses use `application/problem+json` with `status`, `title`, `type`, `instance`, `code`, and `traceId`; validation errors include an `errors` field dictionary where a field can be identified. Stable codes include `validation_failed`, `invalid_json`, `unsupported_media_type`, `body_too_large`, `label_not_found`, `identity_conflict`, `precondition_required`, `stale_version`, `unauthorized`, `loopback_required`, `origin_not_allowed`, `http_error`, and `internal_error`. Unexpected exceptions are logged server-side and sanitized in responses.
- Every API/OpenAPI route requires the configured bearer key. The public `/admin` shell and assets contain no catalog data or key. Local HTTP is an initial development policy, not production authentication. Key comparison uses fixed-time hash comparison. Keys must be at least 32 non-whitespace characters and belong in environment/user secrets, never committed configuration. The host does not issue/store keys.
- Kestrel binds loopback on port 5187 by default (`CatalogApi:Port` override). It rejects custom Kestrel endpoint configuration, remote peer addresses, and non-loopback Host headers (`host_not_allowed`). No forwarded-header/proxy support is enabled. Browser Origin must exactly match the request's own origin or `CatalogApi:AllowedOrigins`; configured origins must be canonical HTTP(S) loopback origins without paths or user info. Allowed-origin preflights do not require a key, but every subsequent API request does. CORS only allows GET/POST/PATCH with Authorization/Content-Type/If-Match, exposes ETag/Location, and does not allow credentials/cookies.
- `CatalogApi:DatabasePath` must select an existing absolute canonical catalog path. Startup verifies label query readiness without seeding/migrating. The API must not be pointed at the scanner inventory database. The live catalog is not touched by automated tests.
- OpenAPI is generated from endpoints and explicit DTOs at `/openapi/v1.json`, including paging fields, bearer security, merge-patch content type, errors, and concurrency headers. Swagger generation uses Swashbuckle for the existing .NET 8 target; no interactive UI or public documentation route is exposed.

[HTTP integration tests](../tests/TrackStash.Catalog.Tests/LabelApiTests.cs) exercise temporary databases, access failures, malformed inputs, paging, identity preservation, legacy-write invalidation, competing edits, child reads, sanitized failures, OpenAPI, and refusal to migrate at startup. A real Kestrel loopback smoke test also verified all six operations. The [`.http` examples](../src/TrackStash.Catalog.Api/TrackStash.Catalog.Api.http) are the manual request harness.

The [React-admin application](../src/TrackStash.Catalog.Admin/README.md) implements label list/detail/create/edit with paged search/sorting and read-only aliases/references. A thin data provider translates the API shape; forms preserve the original read ETag, whitelist mutable fields, and use pessimistic saves. Stale writes display an explicit warning rather than automatically fetching a fresh token/retrying. Bearer keys and UI preferences live in memory only, logout clears cached catalog state, reload requires login again, and React-admin telemetry is disabled.

Production builds are served at `/admin/` by the API when built assets are present. Vite development uses localhost:5173 with a local API proxy and explicit origin configuration. [Unit tests](../src/TrackStash.Catalog.Admin/src/catalog.test.ts) and [browser tests](../src/TrackStash.Catalog.Admin/e2e/labels.spec.ts) verify transport, authentication, list navigation, edits, and stale-write preservation using temporary catalogs.

Remaining work includes CLI adoption of the service interface, the PowerShell HTTP client, relationship/lifecycle UI, authenticated remote deployment, and create idempotency. Do not automatically retry POST until the latter has a persisted design.

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

## Label Implementation Readiness Inventory

### Implemented Foundation

[LabelService](../src/TrackStash.Catalog.Application/LabelService.cs) now provides label query, exact detail, create, and conditional update workflows. Its project references core only, accepts an injected `IStorageProvider`, owns each operation's transaction, and neither accepts database paths nor migrates on startup. It is available for CLI and HTTP composition; existing CLI commands have not been rewired.

The [core contracts](../../trackstash-core/src/TrackStash.Core/Storage/LabelQueries.cs) establish:

- `LabelQuery`/`LabelPage`: one-based pages, default 25, maximum 100, matching total even beyond the last page, and lightweight items without child collections/payloads.
- Search is a literal substring after shared strict normalization of the query and actual label/alias names (including aliases without stored normalization). It is not SQL wildcard, loose, fuzzy, or embedding search. Whitespace/punctuation-only queries are rejected.
- Optional exact normalized-name and provider filters combine with search using AND. `Source` can stand alone; `ExternalId` requires it. Provider comparisons are case-sensitive. Matching multiple aliases/references does not duplicate a label.
- Sort fields are name, sort name, or ID; ascending by default. String ordering is case-sensitive, nulls first ascending/last descending, with ascending ID as the tie-breaker. SQLite uses its binary ordering.
- Queries preserve all same-name or same-provider-reference candidates; they never select one as an identity decision. Legacy single-result lookup methods remain unchanged and must not be used to infer identity in the new surface.
- New service-created labels receive lowercase 32-character GUIDs. Existing IDs are returned and addressed verbatim, not rewritten or coerced. Create only accepts name/sort name, trims the name, computes normalization, and sets timestamps. The repository's create-only operation returns false on ID conflict without touching the existing aggregate; the service reports `Conflict`.
- `LabelUpdate` explicitly distinguishes an omitted field from a supplied value using `SetName`/`SetSortName`. Name cannot be null/blank; sort name can be cleared with null. Empty patches and values without their Set flag are invalid. An update never inserts a missing label and preserves IDs, creation time, payloads, aliases, references, and omitted fields.
- Successful updates return fresh detail and version. Missing and stale updates return distinct results without changes. Invalid input raises an argument exception with a parameter/message; provider failures propagate to the HTTP error handler.
- Detail projects aliases and references without raw provider payloads. Version is an opaque aggregate token, not a timestamp or public numeric counter.

SQLite migration **3, `label-aggregate-version`**, backfills existing labels without changing their IDs or metadata. Database triggers refresh opaque tokens for label insertion/scalar updates and alias/reference insertion/update/deletion, including existing upserts, YAML replace cleanup, and restore. Conditional updates compare ID and version in the UPDATE statement, not an earlier application-level read. A rolled-back edit also rolls back its token. Restore generates a new token so pre-deletion edits cannot be replayed.

Legacy upserts remain unconditional for compatibility; they invalidate tokens but do not themselves reject stale callers. Child-only changes invalidate the aggregate token without promising a change to `updated_utc`. Future adapters must implement equivalent atomic comparison and invalidation for every supported writer; future relationship editing must explicitly define whether it joins this aggregate.

Apply migrations explicitly through the existing bootstrap workflow before using the new code against an existing catalog. Only temporary databases were migrated during implementation; the live catalog and scanner databases were not changed.

[Storage tests](../../trackstash-core/tests/TrackStash.Core.Tests/LabelRepositoryTests.cs) and [application tests](../tests/TrackStash.Catalog.Tests/LabelServiceTests.cs) cover the foundation, including competing writers. The local HTTP mapping is now specified and tested above; remote deployment and client integration remain future work.

### Pre-foundation Inventory

The following inventory records the starting point before the foundation above. Remaining child mutation, relationship, deletion, and HTTP gaps are not implicitly resolved by the new service.

### Reusable Building Blocks and Gaps

| Area | Existing implementation | Gap for the label API |
| --- | --- | --- |
| Identity and normalization | Core `EntityId` and `EntityNameNormalizer`; label DTO/model can carry canonical ID, name, sort name, timestamps | Reconcile GUID serialization across CLI import and API paths; enforce immutable IDs and server-owned normalization in application services |
| Exact label read | `ILabelRepository.GetByIdAsync`; SQLite implementation loads aliases and external references | Add API/application result mapping, missing-resource handling, and aggregate version; do not expose raw provider payloads by default |
| Paged list/search | Repository exposes unfiltered `CountAsync` | No list, filtered count, alias search, stable sort, or pagination contract; implement in the provider boundary rather than loading all labels in the host |
| Candidate lookup | `GetByNormalizedNameAsync` and `GetByExternalRefAsync` | Both return one result; SQLite uses `LIMIT 1`. They cannot report ambiguous identities and are not suitable as authoritative duplicate-resolution methods for the new API |
| Creation/update | Transactional `UpsertAsync` persists label fields and adds/updates supplied aliases/references | No create-only collision result, missing-on-update result, field patch semantics, or expected-version check. API updates must not accidentally insert or clear omitted metadata |
| Alias/reference inspection | Exact label read hydrates child collections | Safe to project read DTOs; avoid per-label child hydration for list responses |
| Alias/reference editing | Aggregate upsert adds/updates child rows | No dedicated remove operation or child version handling. Define child-resource identity and explicit mutations before exposing editing |
| Provider-reference integrity | Composite key is `(label_id, source, external_id)` | Same provider reference can be attached to multiple labels; the index on `(source, external_id)` is not unique. Detect existing conflicts and design transactional enforcement before exposing reference mutations |
| Atomic concurrency | `IUnitOfWork` provides commit/rollback; SQLite creates transactions | Neither label model nor schema has a revision token or compare-and-write operation. A transaction alone does not establish stale-edit detection |
| Safe deletion | Shared dependency analysis, owned cleanup, deletion, tombstone and restore contracts/services | Reuse domain checks, but map physical table/column diagnostics to public relationship names; add HTTP preconditions without bypassing existing checks |
| Label relationships | Core label design describes parent/imprint/brand/distribution relationships | No label relationship model/repository/table in the inspected implementation; separate persistence and validation increment |
| Shared application layer | `CatalogCommands`, core import service, and YAML entity services already compose repositories | Existing command requests carry database/file paths, and YAML applies desired-state aggregates. Extract provider-bound label use cases rather than wrap file/CLI operations as HTTP |
| Host/client infrastructure | Existing catalog is a local executable; offline artifact module has a separate purpose | No API host, HTTP DTOs, authentication, ETags, OpenAPI, React-admin data provider, or PowerShell HTTP client yet |

### Existing Behavior Not to Reuse Blindly

- Normalized names are indexed, not unique. The label seed review already identified identical names with different provider IDs. A label name is not an immutable identity.
- Existing import and YAML resolution can reuse the first normalized-name match. New interactive API creation must not inherit that behavior as an implicit merge or arbitrary selection.
- `UpsertAsync` updates nullable core fields and provider payloads from the supplied model. A name-only edit must first preserve untouched state and must use an update-specific concurrency-aware operation.
- Alias/reference upsert is additive; omitting a child collection does not remove existing children. Conversely, YAML `replace` explicitly cleans owned rows. Neither behavior should implicitly determine HTTP patch semantics.
- Current delete DTOs report database table/column names. These are useful for local administration but not an appropriate provider-independent public error contract.
- Existing entity IDs are strings, including legacy/example values. Freezing a GUID wire format does not authorize rewriting persisted identities.
- All writers must participate in the revision policy: HTTP, local CLI, imports, alias/reference mutations, and any restore workflow. An API-only revision counter would miss edits made through other paths.

### First Engineering Increment (Now Implemented)

The foundation above implements this minimum before adding controllers:

1. A paged label query/result with explicit search/filter/sort fields, total count, and stable ID tie-breaker.
2. Exact label detail projection with aliases/provider references and an opaque version.
3. Distinct create and conditional-update outcomes (created, missing, stale, invalid, conflict), preserving unrelated state.
4. A persisted revision and atomic expected-version update, with a plan for existing writers and migration compatibility.
5. A catalog application service accepting an injected storage provider, not a caller-supplied database path.

The storage implementation preserves same-name labels and exposes candidate ambiguity rather than inferring identity from text. Relationship editing, child mutations, merges, and deletion endpoints remain outside this increment. Existing deletion/restore currently derives tombstone content from `source_payload_json`, not a full relational snapshot; verify and correct that limitation before offering safe lifecycle endpoints.

### Contract Decisions Resolved for the Initial Host

The initial host resolves these choices as described above:

- New canonical GUID output and verbatim legacy IDs, without changing stored IDs.
- Shared search semantics, sortable fields, page limits, and all ambiguous provider-reference candidates.
- Strict JSON Merge Patch allowlist/null handling and aggregate revisions across existing writers.
- Loopback-only development hosting with a bearer key and exact same-origin/configured loopback browser origins. Remote deployment is not supported.

Future remote access requires a separate explicit access policy. The initial host does not ship an unauthenticated API mode.

### Source and Test Anchors

- [Core storage contracts](../../trackstash-core/src/TrackStash.Core/Storage/Contracts.cs)
- [Core models](../../trackstash-core/src/TrackStash.Core/Storage/Models.cs)
- [Normalization](../../trackstash-core/src/TrackStash.Core/Normalization/EntityNameNormalizer.cs)
- [GUID helper](../../trackstash-core/src/TrackStash.Core/Identifiers/EntityId.cs)
- [SQLite repositories](../../trackstash-core/src/TrackStash.Core.Sqlite/Repositories.cs)
- [SQLite migrations](../../trackstash-core/src/TrackStash.Core.Sqlite/Migrations.cs)
- [SQLite unit of work](../../trackstash-core/src/TrackStash.Core.Sqlite/SqliteUnitOfWork.cs)
- [Delete contracts](../../trackstash-core/src/TrackStash.Core/Storage/DeleteContracts.cs)
- [Catalog facade](../src/TrackStash.Catalog/CatalogCommands.cs)
- [YAML apply service](../src/TrackStash.Catalog/Entities/EntityYamlService.cs)
- [Existing import integration tests](../tests/TrackStash.Catalog.Tests/ImportCsvIntegrationTests.cs)
- [Existing deletion integration tests](../tests/TrackStash.Catalog.Tests/DeleteEntityIntegrationTests.cs)

Paths above are relative to this document's directory. Existing catalog tests cover imports, summary, deletion and command-line flows. Focused tests cover foundation service/adapter behavior, HTTP contracts, concurrent conditional updates, and GUI workflows using temporary catalogs; PowerShell client tests remain future work.

## Implementation Plan and Acceptance Gates

1. Inventory reusable catalog/core contracts and gaps: paged search/count, safe updates, atomic versioning, alias/reference operations, relationship persistence, and diagnostics.
2. Freeze the label DTO, query, patch, identity-format, concurrency, error, and access contracts; document examples/OpenAPI expectations.
3. Extract only the shared application use cases needed; implement/test provider-independent services and SQLite adapter support.
4. Add `TrackStash.Catalog.Api` with local configuration, explicit storage selection, OpenAPI, and label endpoints. Test against a temporary catalog, not the live scanner database.
5. Add the minimal React-admin label list/detail/create/edit screens and PowerShell client against the same API.
6. Add label relationship persistence and workflows, then safe deletion and duplicate/merge review as separate increments.
7. Expand to other entities after the label contract has proven usable.

Tests must cover pagination totals/stable sorting, validation, normalization, immutable IDs, create/reapply identity behavior, stale edits, transactional rollback, missing resources, aliases/provider-reference ambiguity, safe deletion, and client error handling. Add relationship direction/cycle and merge-redirect tests with those capabilities. Verify UI and PowerShell behavior, not just repository methods.

Before expanding the host to remote deployment, settle its access policy rather than assuming the local bearer-key policy is sufficient. Planning does not authorize changes to existing catalog data, media files, or running Library jobs.
