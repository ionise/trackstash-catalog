# trackstash-catalog TODO

## Image-aware entity workflows (planned)

- [ ] Extend entity YAML contract usage to carry image links/roles provided by core contracts
- [ ] Add catalog apply/get orchestration for entity images without direct SQL/database access in catalog
- [ ] Support recording image release-context linkage when provided by the input model
- [ ] Surface fallback behavior in get/export flows (recording -> parent release image when no recording-specific image exists)
- [ ] Add dry-run/apply reporting updates so image add/update/remove operations are visible
- [ ] Add integration tests for image apply/get flows against core contract implementations
- [ ] Keep command behavior provider-agnostic through `IStorageProviderFactory` and core interfaces
