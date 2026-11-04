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
- [ ] Implement and test label relationship persistence, direction, provenance, effective dates, and hierarchy validation
- [ ] Add safe deletion preview/apply and reviewed duplicate/merge workflows with retired-ID resolution (first ensure tombstones snapshot relational state, not just provider payloads)
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
