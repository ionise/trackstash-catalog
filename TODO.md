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
- [ ] Implement local artwork upload/serving with source and usage-rights controls
- [ ] Design a separate pluggable enrichment module and reviewed observation acceptance; automatic fetching is parked
- [ ] Define label embedding documents, invalidation/regeneration, and semantic search (exclude personal notes by default)
- [x] Document reviewed pairwise label merge design, multiple provider IDs, conflict policies, audit, concurrency, and retired-ID resolution (see [merge design](docs/api-design.md#reviewed-label-merge-design))
- [x] Implement reviewed label merge storage/application contracts, preview/apply API, GUI confirmation, and all-writer retired-ID safeguards with temporary-catalog tests
- [ ] Add safe deletion preview/apply (first ensure tombstones snapshot relational state, not just provider payloads, and preserve retired-ID resolution)
- [ ] Validate the label slice end to end before expanding to artists, releases, and recordings
- [ ] Design genres/subgenres and ordered playlists later without duplicating recording identities or assuming local file ownership

## Image-aware entity workflows (planned)

- [ ] Extend entity YAML contract usage to carry image links/roles provided by core contracts
- [ ] Add catalog apply/get orchestration for entity images without direct SQL/database access in catalog
- [ ] Support recording image release-context linkage when provided by the input model
- [ ] Surface fallback behavior in get/export flows (recording -> parent release image when no recording-specific image exists)
- [ ] Add dry-run/apply reporting updates so image add/update/remove operations are visible
- [ ] Add integration tests for image apply/get flows against core contract implementations
- [ ] Keep command behavior provider-agnostic through `IStorageProviderFactory` and core interfaces
