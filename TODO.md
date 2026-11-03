# trackstash-catalog TODO

## Label-first API and clients (planned)

See [API design intent](docs/api-design.md) for contracts, boundaries, open decisions, and acceptance gates.

- [ ] Inventory shared service/storage gaps for label queries, updates, aliases, provider references, and atomic concurrency
- [ ] Settle GUID serialization, DTOs, pagination/filtering, patch semantics, Problem Details, and access policy
- [ ] Extract the shared catalog application use cases needed by CLI and HTTP host
- [ ] Add `TrackStash.Catalog.Api` with provider-independent label list/read/create/edit and alias/reference inspection
- [ ] Add OpenAPI and integration/contract coverage against temporary databases
- [ ] Add React-admin label screens and a separate `TrackStash.Catalog.Client` PowerShell HTTP module
- [ ] Implement and test label relationship persistence, direction, provenance, effective dates, and hierarchy validation
- [ ] Add safe deletion preview/apply and reviewed duplicate/merge workflows with retired-ID resolution
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
