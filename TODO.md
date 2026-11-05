# trackstash-catalog TODO

## Label-first API and clients

See [API design intent](docs/api-design.md) for contracts, boundaries, open decisions, and acceptance gates.

- [x] Inventory shared service/storage gaps for label queries, updates, aliases, provider references, and atomic concurrency (see [readiness inventory](docs/api-design.md#label-implementation-readiness-inventory))
- [x] Build provider-independent label query/detail/create/conditional-update foundation with SQLite aggregate versioning and temporary-database tests
- [x] Settle initial GUID serialization, DTOs, pagination/filtering, patch semantics, Problem Details, and loopback/bearer-key access policy
- [x] Integrate shared label application services into HTTP host
- [ ] Integrate shared label application services into CLI; extract further use cases as needed
- [x] Add `TrackStash.Catalog.Api` with provider-independent label list/read/create/edit and alias/reference inspection
- [x] Add OpenAPI and integration/contract coverage against temporary databases
- [x] Add React-admin label list/detail/create/edit screens with memory-only login, origin policy, ETags, and browser tests
- [ ] Add a separate `TrackStash.Catalog.Client` PowerShell HTTP module
- [x] Add manual label profiles, typed web links, personal metadata, immutable revisions, API/Edit/Show, and profile-aware reviewed merges
- [x] Prepare source-attributed observations, local artwork with source/rights, and typed label relationship schema (migration 5)
- [x] Implement reviewed imprint/distributor relationships, inverse views, hierarchy validation, permanent history and relationship-aware merge preview/transfer (migration 7); dates/territory remain excluded
- [x] Design distinct family/organisation label groups with flat memberships, evidence/date/status qualifiers, both-endpoint editing and derived related-label semantics (see [group contract](docs/label-groups-design.md))
- [x] Implement label groups/memberships, reviewed merge transfer, paged group/related-label views and schema 6/7/8 backup compatibility following the [delivery gates](docs/label-groups-design.md#8-delivery-and-acceptance)
- [x] Support reviewed child-imprint/distributed-label editing from the parent/distributor page, storing one directed edge and preserving unrelated relationships
- [x] Activate migration 8 on the live catalog only after a verified backup, rehearsal and separate explicit approval
- [x] Deliver label artwork as the first consumer of shared artwork infrastructure (stages below)
- [x] Polish the Label detail UI: identity header, preferred logo with accessible artwork action, biography, favourite toggle, external-link icons, local country flags, secondary record details, and honest future-content placeholders (see [UI plan](docs/label-detail-ui-plan.md))
- [ ] Once related entity/query infrastructure exists, add label-scoped Tracks, Releases and related Artists subpages with backend search/filtering, stable sorting, pagination and URL query state (see [longer-term list plan](docs/label-detail-ui-plan.md#longer-term-searchable-subpages))
- [ ] Design a separate pluggable enrichment module and reviewed observation acceptance; automatic fetching is parked
- [ ] Define label embedding documents, invalidation/regeneration, and semantic search (exclude personal notes by default)
- [x] Document reviewed pairwise label merge design, multiple provider IDs, conflict policies, audit, concurrency, and retired-ID resolution (see [merge design](docs/api-design.md#reviewed-label-merge-design))
- [x] Implement reviewed label merge storage/application contracts, preview/apply API, GUI confirmation, and all-writer retired-ID safeguards with temporary-catalog tests
- [ ] Add safe deletion preview/apply (first ensure tombstones snapshot relational state, not just provider payloads, and preserve retired-ID resolution)
- [ ] Validate the label slice end to end before expanding to artists, releases, and recordings
- [ ] Design genres/subgenres and ordered playlists later without duplicating recording identities or assuming local file ownership

## Reviewed purchase-to-release catalog import

See the [data-grounded release import design](docs/purchase-release-import-design.md). This is the next catalog increment: minimum Artist/Release/Recording support together, using one release as the reviewed entry point, not a wholesale artist import. Scanner media resolution remains deferred.

- [x] Inspect purchase corpus derivation, raw release/track JSON, structured artist/remixer identities, label reuse and current canonical constraints
- [x] Design complete-release acquisition, artist identity/credit separation, recording/placement semantics, reviewed apply and migration/backup acceptance gates
- [x] Confirm cache-first review, optional official numbering/manual display order, unresolved compound acceptance and later reviewed splitting; document canonical embedding lifecycle and transactional durable work
- [x] Build a read-only corpus-flag queue and release-bundle inspector with explicit source completeness, unverified numbering and identity diagnostics, authenticated API and GUI; verify Block House without source/catalog writes
- [ ] Add receipt-backed queue ingestion and deliberate bounded provider fetch/refresh after verifying adapter permissions/authentication/semantics; initial inspector uses cached corpus candidates only
- [x] Implement minimal Artist/Release/Recording/placement/credit models, conditional versions and conflict-safe provider bindings using forward migration 9, tested on temporary databases only
- [ ] Implement immutable import reviews and atomic idempotent apply, preserving curator decisions and permanent provenance/audit
- [x] Implement conditional manual placement reordering in shared storage, retaining stable IDs, source positions and official numbers
- [ ] Wire manual ordering into reviewed reimport reconciliation and GUI/API without resetting curator order
- [ ] Prepare versioned Artist/Release/Recording embedding documents, dependent invalidation and transaction-enlisted durable work; activate and verify local generation separately after commit
- [x] Preserve unresolved compound identity state and stable source-credit expression/path/evidence in the initial foundation
- [ ] Deliver reviewed one-to-many Artist split resolution/editor with per-occurrence reconciliation (not provider-ID duplication or a merge redirect)
- [ ] Deliver and verify one complete release end to end, then cover collaborators, compound identities, reissues and incomplete manifests before batching
- [ ] Activate any future schema only after backup verification, rehearsal and separate live approval

## User identity before private purchase history

See the [single-user-first identity design](docs/user-identity-design.md). Shared catalog work can proceed; receipt-backed private queues and purchase history require this foundation first.

- [x] Design stable user/authentication bindings, separate private ownership and actor attribution, future multi-user boundaries and migration/authorization gates
- [ ] Implement forward user/identity schema and explicit local-owner provisioning; bind the existing server credential to an active user without storing its secret
- [ ] Pass authenticated actor context through private workflows and enforce owner-scoped queries/operations before purchase-history ingestion
- [ ] Preserve historical unknown actors; add verified attribution for new writes without rewriting immutable history
- [ ] Before admitting a second user, define shared-catalog permissions, private settings migration and whole-catalog backup operator access

## Shared artwork infrastructure

See [shared artwork design intent](../trackstash-core/docs/artwork-design.md) for decisions, implementation status, dependencies, and acceptance gates. Migration 6 implements shared assets with configurable managed files outside Git, protected uploads/thumbnails, and label galleries. Finish Labels before Artists; Releases come after label and artist workflows are ready.

- [x] Record shared assets, foreign-key-backed entity associations, galleries, and explicit release-context fallback intent
- [x] A: Specify shared asset and label association contracts, provenance/rights boundaries, managed-file storage, roles/order/preference, protected browser serving, processing limits, retention, and concurrency (recording resolution remains D)
- [x] B: Implement shared persistence and a new forward migration from populated version 5; preserve legacy artwork and install dependency/version/retirement safeguards
- [x] C: Deliver validated local upload, thumbnails, authenticated serving, and reusable label artwork/gallery UI with source/rights, atomic replace/detach, ordering, and preferred selection
- [x] C: Test survivor-first gallery stacking, preferred-image policy, provenance/history/audit and stale merge rejection; retain safe deletion blockers until full snapshot/restore support exists
- [x] Add native catalog artwork backup/restore: coordinate consistent DB snapshots and the configured managed-file root, include retained assets/history and a checksum manifest, verify missing/corrupt originals, and test restore (database-only backups are insufficient; see [backup operations](docs/catalog-backups.md))
- [x] Expose the same shared backup operation through CLI and authenticated admin GUI/API: create and verify a ZIP in a server-configured filesystem destination, with a UTC timestamp in the filename (for example `trackstash-catalog-20261104T173702Z-<backup-id>.zip`); use a unique backup ID to avoid same-timestamp collisions and never overwrite an existing archive
- [x] Add an admin Backups page to trigger jobs, show progress/results and offer authenticated download of completed ZIPs already saved on disk; download exports the same archive rather than creating a second backup. Include catalog snapshot, retained artwork and versioned checksum manifest, exclude credentials/unrelated media, and define temporary-file cleanup, newest-10 verified retention and large-download handling. Keep restore to a separate destination via CLI initially; downloading alone does not establish a NAS backup
- [x] Add separately configured scanner inventory ZIP backup via shared native snapshot/archive services, CLI and System Backup type selection, with exact database preservation, independent retention, authenticated download and safe separate-directory restore; exclude external media and preserve catalog archive compatibility
- [ ] Add deliberate orphan reconciliation and thumbnail regeneration; do not prune retained history assets or perform automatic destructive cleanup
- [ ] D (after B and relevant entity workflows): Reuse assets/associations for artists and releases; implement recording overrides, release-context resolution, explicit preferred-release fallback, and missing/ambiguous results
- [ ] D: Verify original-release/compilation/reissue context and release-change cache invalidation; never pick an arbitrary first release or copy inherited artwork onto recordings
- [ ] E (after shared contracts and applicable workflows): Extend portable YAML/import/export and CLI dry-run/apply/get through provider-independent services; define binary packaging and distinguish inherited from explicit artwork
- [x] Validate the label artwork increment with core/API/browser tests and an approved live pre-v6 backup, migration and read-only smoke check; future increments require their own validation/upgrade approval

No automatic remote fetching, enrichment, media writeback, or full multi-entity GUI expansion is included in the initial label slice.
