# TrackStash Catalog Entity Templates (v1)

This directory defines a YAML desired-state contract for canonical catalog entities.
The templates in `../../templates/entities` are intended to be:

- human-editable
- scriptable from bash/PowerShell wrappers
- deterministic for upsert/reconcile behavior
- stable enough to use as an export/import interchange format

## How Users Apply Files

Validate first:

```bash
trackstash-catalog validate-entity --file ./templates/entities/label.v1.yaml
```

Preview changes:

```bash
trackstash-catalog apply-entity --file ./templates/entities/label.v1.yaml --dry-run --output json
```

Apply desired state:

```bash
trackstash-catalog apply-entity --file ./templates/entities/label.v1.yaml --output json
```

## Shared Envelope

All entity files use this envelope:

```yaml
apiVersion: catalog.trackstash/v1
kind: Label | Artist | Release | Recording
mode: replace
metadata:
  id: optional-stable-id
spec:
  ...entity payload...
```

## Mode Semantics

Recommended modes for v1:

1. `replace`
- Treat the file as complete desired state for the entity.
- Upsert core row.
- Reconcile relationship and child collections exactly to match file content.
- Missing rows in DB are inserted, extra rows in DB are removed.

2. `merge`
- Upsert core row.
- Add/update collection entries present in file.
- Do not remove existing collection entries that are omitted from file.
- Useful for additive workflows and partial source sync.

3. `create-only`
- Insert only; fail if entity already exists.
- Useful when strict first-write provenance is required.

4. `update-only`
- Update only; fail if entity does not exist.
- Useful for controlled mutation workflows.

`replace` should remain the default mode for deterministic reconciliation.

## Full-Schema Notes

Templates include full core schema fields exposed by current SQLite storage:

- core columns (`id`, name/title/sort/mix/isrc, normalized values, payload JSON, timestamps)
- aliases
- external references
- release/recording artist credits
- release label links
- release-recording links
- recording relationships

For fields typically owned by storage/runtime (for example `createdUtc` and `updatedUtc`), templates include them as optional for import/export parity.

## Export Interface Recommendation

When `get-entity --output yaml` is implemented, it should emit this same schema.
That enables export-edit-reapply loops and cross-tool handoff without format conversions.
