# Catalog ZIP backups

The shared native backup service supports the catalog CLI and the authenticated admin GUI/API. It currently supports SQLite catalog schema 6. It does not migrate a database, back up scanner inventory, copy audio/media files, or restore over a live catalog.

## What is saved

Each backup is a ZIP containing:

- `catalog.db`: a consistent SQLite online-backup snapshot, including profiles, audit/history, retired identities and all other catalog tables.
- `artwork/<asset-id>/original`: every immutable asset in the snapshot's artwork inventory, including detached/replaced assets retained for history.
- `artwork/<asset-id>/thumbnail.jpg`: existing thumbnails. Missing thumbnails are optional; the artwork service can derive them from originals on read.
- `manifest.json`: format version 1, backup ID, UTC creation timestamp, catalog key, schema version, and SHA-256/byte length for each database/image file.

Only database-registered assets are included; uncommitted/orphan directories are not. Credentials/configuration files and unrelated media files are not included. Catalog content itself may contain private notes and provider payloads: ZIPs are **not encrypted** and should be treated as private.

The filename is `trackstash-catalog-YYYYMMDDTHHMMSSZ-<unique-id>.zip`. The timestamp is UTC; the ID prevents collisions within the same second. Existing files are never overwritten.

## Destination and retention

The default is `<LocalApplicationData>/TrackStash/backups`, resolved for the current server user. On macOS this is normally `~/Library/Application Support/TrackStash/backups`.

- API override: `CatalogApi__BackupRoot`.
- Shared environment override: `TRACKSTASH_BACKUP_ROOT`.
- CLI override: `--backup-root <absolute-directory>`.
- Artwork input: the API's existing `CatalogApi__ArtworkRoot`, or `TRACKSTASH_ARTWORK_ROOT`/platform default; CLI also accepts `--artwork-root`.

The API-specific root wins over the shared environment/default. Paths must be outside Git. New directories/files use private user-only Unix permissions. Existing directories retain their permissions; choose an appropriately protected destination. A NAS destination must be mounted and writable before starting; the operation does not mount shares or manage NAS credentials.

Retention keeps the newest **10 verified backups per catalog**, pruning only after a new archive has been completely verified and published. Archives are verified before counting toward that limit or being removed. Unrelated filenames, other catalog keys and unverifiable archives are not deliberately pruned. Failed cleanup produces explicit warnings; a verified new archive remains available. A corrupt manifest can prevent inventory/retention from running and must be investigated, not silently discarded.

The catalog key is a SHA-256 digest of the absolute source database path. Moving the source database changes its retention/listing group. This key is operational grouping, not a canonical entity identity or a signature.

There is no schedule yet. GUI and CLI provide manual triggers. There is no automatic permanent artwork cleanup.

## Admin GUI

Open **System → Backup** in the sidebar (the existing `/backups` admin route remains valid):

1. Select **Create backup**. The server snapshots, packages and verifies it in the configured filesystem destination.
2. Observe progress and the completed/failed result. Missing or corrupt originals fail the backup rather than produce a successful incomplete archive.
3. Use **Download ZIP** to export that same saved archive. Download does not create another backup or remove the server copy.
4. Use **Refresh backups** to see completed archives created by the CLI.

Jobs continue after the initiating browser request disconnects. Job progress is process-local, bounded to the latest 100 jobs and resets on server restart. Completed ZIPs are persistent; list refresh discovers them after restart. Server shutdown interrupts unfinished jobs; retry after restart.

Before the first backup, the page shows an empty list without an error. If it reports that the running API does not provide backup support, restart the API with the updated backend and refresh the list; rebuilding the GUI alone does not update an already-running server.

Download uses bearer-authenticated fetch, never API keys in URLs. Where Save File support exists, bytes stream to the selected file. Other browsers use a bounded in-memory download up to 128 MiB; for larger archives use a compatible browser or retrieve the ZIP from the server filesystem. Only completed archives are downloadable, and checksums are verified again before sending.

A filesystem lease serialises create/verify/restore/download operations across CLI/API processes using the same backup root. A download holds that lease until its stream closes, so a backup cannot prune it. Busy operations fail explicitly and can be retried after the current operation finishes. Listing does not acquire the lease. Independent roots are independent operations.

## CLI

From the repository root (replace example paths):

```bash
dotnet run --project trackstash-catalog/src/TrackStash.Catalog -- backup \
  --db-path /absolute/path/Trackstash.Catalog.db \
  --artwork-root /absolute/path/artwork-data \
  --backup-root /absolute/path/backups --output json

dotnet run --project trackstash-catalog/src/TrackStash.Catalog -- verify-backup \
  --file /absolute/path/backups/trackstash-catalog-TIMESTAMP-ID.zip \
  --backup-root /absolute/path/backups --output json

dotnet run --project trackstash-catalog/src/TrackStash.Catalog -- restore-backup \
  --file /absolute/path/backups/trackstash-catalog-TIMESTAMP-ID.zip \
  --destination /absolute/path/restored-catalog \
  --backup-root /absolute/path/backups --output json
```

`backup` uses existing catalog/provider configuration resolution. Verification and restore require no live database. Nonzero exit status and structured errors indicate failure.

Restore requires a **new destination directory with an existing parent**, outside Git. It validates all paths, exact inventory, declared sizes, checksums, SQLite integrity/foreign keys, schema and artwork inventory, extracts to private sibling staging, then publishes the new directory. It never replaces existing data. The result is `catalog.db` plus `artwork/`; point a separately stopped/reconfigured API at both paths only after reviewing the restore.

Verification/restore currently accept format 1/schema 6, at most 100,000 manifest files, a 16 MiB manifest and 100 GiB declared uncompressed data. Streamed extraction enforces declared per-file limits. Archive checksums detect damage, not malicious authorship; only restore trusted backups. No GUI restore-over-live operation is exposed.

## Consistency and failure boundaries

SQLite's online backup API produces the database snapshot; normal catalog writers need not be stopped. Snapshot metadata determines which immutable artwork files are copied. Files committed after that snapshot are not required by it. This relies on current immutable/no-delete asset storage and the absence of permanent artwork GC; future GC must coordinate with backup leases/snapshots.

Database integrity, foreign keys, every original's catalog checksum/length, and all archived file checksums are checked before publication. Thumbnails are retained as bytes and checksum-verified, not independently image-decoded. Missing originals and checksum mismatch fail the operation. Ordinary failures remove only their own staging paths and do not prune older backups.

Completed archives are published by a non-overwriting rename within the destination filesystem. Process/power loss may leave `.work-<id>` or `.restore-<id>` staging directories; these are not completed backups and are not automatically deleted by another process. Investigate/remove only known abandoned staging with no operation running. SQLite snapshot calls are synchronous; cancellation is checked around that call and during subsequent streaming work.

Saving/downloading locally is not proof of a NAS backup. Use a configured NAS destination or a separately verified transfer. Backups and restores are not performed on live user data by the test suites.
