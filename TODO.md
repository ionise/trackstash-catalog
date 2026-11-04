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
- [ ] Deliver label artwork as the first consumer of shared artwork infrastructure (stages below)
- [ ] Design a separate pluggable enrichment module and reviewed observation acceptance; automatic fetching is parked
- [ ] Define label embedding documents, invalidation/regeneration, and semantic search (exclude personal notes by default)
- [x] Document reviewed pairwise label merge design, multiple provider IDs, conflict policies, audit, concurrency, and retired-ID resolution (see [merge design](docs/api-design.md#reviewed-label-merge-design))
- [x] Implement reviewed label merge storage/application contracts, preview/apply API, GUI confirmation, and all-writer retired-ID safeguards with temporary-catalog tests
- [ ] Add safe deletion preview/apply (first ensure tombstones snapshot relational state, not just provider payloads, and preserve retired-ID resolution)
- [ ] Validate the label slice end to end before expanding to artists, releases, and recordings
- [ ] Design genres/subgenres and ordered playlists later without duplicating recording identities or assuming local file ownership

## Shared artwork infrastructure (planned)

See [shared artwork design intent](../trackstash-core/docs/artwork-design.md) for decisions, open design points, dependencies, and acceptance gates. Only the migration-5 label-specific foundation exists today; shared image contracts are not yet implemented.

- [x] Record shared assets, foreign-key-backed entity associations, galleries, and explicit release-context fallback intent
- [ ] A: Specify core asset/association/resolution contracts, provenance/rights boundaries, storage strategy, roles/order/preference, protected browser serving, processing limits, retention, and concurrency
- [ ] B (after A): Implement shared persistence and a new forward migration from populated version 5; preserve legacy artwork and install dependency/version/retirement safeguards
- [ ] C (after B): Deliver validated local upload, thumbnails, authenticated serving, and reusable label artwork/gallery UI with source/rights, replace/detach, ordering, and preferred selection
- [ ] C: Test artwork-aware merge transfer/conflicts/audit before removing the current conservative blocker; retain safe deletion blockers until full snapshot/restore support exists
- [ ] D (after B and relevant entity workflows): Reuse assets/associations for artists and releases; implement recording overrides, release-context resolution, explicit preferred-release fallback, and missing/ambiguous results
- [ ] D: Verify original-release/compilation/reissue context and release-change cache invalidation; never pick an arbitrary first release or copy inherited artwork onto recordings
- [ ] E (after shared contracts and applicable workflows): Extend portable YAML/import/export and CLI dry-run/apply/get through provider-independent services; define binary packaging and distinguish inherited from explicit artwork
- [ ] Validate each increment with focused core/API tests and browser tests where applicable; approve live backup/migration separately

No automatic remote fetching, enrichment, media writeback, or full multi-entity GUI expansion is included in the initial label slice.
