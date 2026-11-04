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
- [ ] Implement reviewed label relationship workflows and hierarchy validation; initial schema includes direction/source/notes, not dates or territory
- [x] Deliver label artwork as the first consumer of shared artwork infrastructure (stages below)
- [ ] Design a separate pluggable enrichment module and reviewed observation acceptance; automatic fetching is parked
- [ ] Define label embedding documents, invalidation/regeneration, and semantic search (exclude personal notes by default)
- [x] Document reviewed pairwise label merge design, multiple provider IDs, conflict policies, audit, concurrency, and retired-ID resolution (see [merge design](docs/api-design.md#reviewed-label-merge-design))
- [x] Implement reviewed label merge storage/application contracts, preview/apply API, GUI confirmation, and all-writer retired-ID safeguards with temporary-catalog tests
- [ ] Add safe deletion preview/apply (first ensure tombstones snapshot relational state, not just provider payloads, and preserve retired-ID resolution)
- [ ] Validate the label slice end to end before expanding to artists, releases, and recordings
- [ ] Design genres/subgenres and ordered playlists later without duplicating recording identities or assuming local file ownership

## Shared artwork infrastructure

See [shared artwork design intent](../trackstash-core/docs/artwork-design.md) for decisions, implementation status, dependencies, and acceptance gates. Migration 6 implements shared assets with configurable managed files outside Git, protected uploads/thumbnails, and label galleries. Finish Labels before Artists; Releases come after label and artist workflows are ready.

- [x] Record shared assets, foreign-key-backed entity associations, galleries, and explicit release-context fallback intent
- [x] A: Specify shared asset and label association contracts, provenance/rights boundaries, managed-file storage, roles/order/preference, protected browser serving, processing limits, retention, and concurrency (recording resolution remains D)
- [x] B: Implement shared persistence and a new forward migration from populated version 5; preserve legacy artwork and install dependency/version/retirement safeguards
- [x] C: Deliver validated local upload, thumbnails, authenticated serving, and reusable label artwork/gallery UI with source/rights, atomic replace/detach, ordering, and preferred selection
- [x] C: Test survivor-first gallery stacking, preferred-image policy, provenance/history/audit and stale merge rejection; retain safe deletion blockers until full snapshot/restore support exists
- [ ] Add native catalog artwork backup/restore: coordinate consistent DB snapshots and the configured managed-file root, include retained assets/history and a checksum manifest, verify missing/corrupt originals, and test restore (database-only backups are insufficient)
- [ ] Add deliberate orphan reconciliation and thumbnail regeneration; do not prune retained history assets or perform automatic destructive cleanup
- [ ] D (after B and relevant entity workflows): Reuse assets/associations for artists and releases; implement recording overrides, release-context resolution, explicit preferred-release fallback, and missing/ambiguous results
- [ ] D: Verify original-release/compilation/reissue context and release-change cache invalidation; never pick an arbitrary first release or copy inherited artwork onto recordings
- [ ] E (after shared contracts and applicable workflows): Extend portable YAML/import/export and CLI dry-run/apply/get through provider-independent services; define binary packaging and distinguish inherited from explicit artwork
- [x] Validate the label artwork increment with core/API/browser tests and an approved live pre-v6 backup, migration and read-only smoke check; future increments require their own validation/upgrade approval

No automatic remote fetching, enrichment, media writeback, or full multi-entity GUI expansion is included in the initial label slice.
