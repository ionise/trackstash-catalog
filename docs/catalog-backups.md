# Catalog and scanner inventory ZIP backups

The shared native backup service supports the catalog CLI and the authenticated admin GUI/API. It supports two separate SQLite backup types: **Catalog + artwork** (catalog schemas 6, 7, 8 and 9) and **Scanner inventory**. It does not migrate either database, copy audio/media files, or restore over a live database. These are database protection operations, not media replication.

Schema 6 and 7 archives remain verifiable/restorable after upgrading to schema 8. Schema 7 snapshots include label relationships and permanent relationship history; schema 8 additionally includes groups, memberships and both permanent group histories automatically. Restore preserves the archived schema; it does not upgrade it. The current API requires schema 8: take a verified pre-upgrade backup and explicitly migrate the catalog before restarting it. Scanner backup/schema handling is unchanged.

Schema 9 archives additionally preserve canonical Artist/Release/Recording metadata and versions, stable multi-role credits, repeated release placements and their provider bindings/source credits, and permanent foundation operation history. Schema 6/7/8 archives remain verifiable/restorable after that upgrade. Migration 9 is tested on temporary databases only and has not been approved/applied live; the label/inspector API still works on schema 8. Live activation requires a separate verified backup, rehearsal and approval.

## What is saved

Each **catalog** backup is a ZIP containing:

- `catalog.db`: a consistent SQLite online-backup snapshot, including profiles, audit/history, retired identities and all other catalog tables.
- `artwork/<asset-id>/original`: every immutable asset in the snapshot's artwork inventory, including detached/replaced assets retained for history.
- `artwork/<asset-id>/thumbnail.jpg`: existing thumbnails. Missing thumbnails are optional; the artwork service can derive them from originals on read.
- `manifest.json`: format version 1, kind (`catalog` or `scanner`), backup ID, UTC creation timestamp, source-path key, schema version, and SHA-256/byte length for each database/image file. Older catalog manifests without a kind remain supported as catalog backups.

Each **scanner inventory** backup contains only `inventory.db` and `manifest.json`. The SQLite online snapshot preserves the entire database, including every table, index, view, trigger and stored value: file locations/hashes, tags, fingerprints, analysis, provider caches, embeddings and any other data stored in that database. No media file, external artwork, sidecar or configuration file is followed/copied. Database content may itself contain sensitive paths or cached payloads.

The scanner has no catalog migration ledger. Validation checks SQLite physical integrity and the stable `media_file`/`metadata` column contract. Existing scanner foreign-key violations are counted and reported as explicit warnings in the job/CLI creation result, saved manifest and verification result; they do not prevent an exact protective backup. Restore preserves those inconsistencies unchanged, rather than repairing or dropping records. Catalog backups still require clean foreign keys. The manifest records SQLite `user_version` (currently normally 0), not catalog schema 6. Additive tables/columns are preserved without a migration. This does not certify future incompatible scanner schemas.

Only database-registered assets are included; uncommitted/orphan directories are not. Credentials/configuration files and unrelated media files are not included. Catalog content itself may contain private notes and provider payloads: ZIPs are **not encrypted** and should be treated as private.

Filenames are `trackstash-catalog-YYYYMMDDTHHMMSSZ-<unique-id>.zip` and `trackstash-scanner-YYYYMMDDTHHMMSSZ-<unique-id>.zip`. The timestamp is UTC; the ID prevents collisions within the same second. Existing files are never overwritten.

## Destination and retention

The default is `<LocalApplicationData>/TrackStash/backups`, resolved for the current server user. On macOS this is normally `~/Library/Application Support/TrackStash/backups`.

- API override: `CatalogApi__BackupRoot`.
- Shared environment override: `TRACKSTASH_BACKUP_ROOT`.
- CLI override: `--backup-root <absolute-directory>`.
- Artwork input: the API's existing `CatalogApi__ArtworkRoot`, or `TRACKSTASH_ARTWORK_ROOT`/platform default; CLI also accepts `--artwork-root`.
- Scanner input: set `CatalogApi__ScannerDatabasePath` to the absolute scanner inventory database path, or use `TRACKSTASH_SCAN_DATABASE_PATH`. The API-specific setting wins. Restart the API after changing configuration. There is deliberately no guessed database path or automatic reading of scanner-local JSON configuration; the GUI never accepts a source path.

For example, in the shell that starts the API:

```bash
export CatalogApi__ScannerDatabasePath="/absolute/path/trackstash-scan.db"
```

Without this setting the catalog backup remains available; scanner backup is shown as unconfigured/disabled. A configured missing/unreadable/wrong-schema database fails explicitly without advertising a completed archive. Do not select the canonical catalog as the scanner source.

The API-specific root wins over the shared environment/default. Paths must be outside Git. New directories/files use private user-only Unix permissions. Existing directories retain their permissions; choose an appropriately protected destination. A NAS destination must be mounted and writable before starting; the operation does not mount shares or manage NAS credentials.

Retention keeps the newest **10 verified backups per source database and backup type**, pruning only after a new archive has been completely verified and published. Catalog and scanner archives may share a destination without pruning or listing each other's backups. Archives are verified before counting toward that limit or being removed. Unrelated filenames, other source keys and unverifiable archives are not deliberately pruned. Failed cleanup produces explicit warnings; a verified new archive remains available. A corrupt manifest can prevent inventory/retention for its type from running and must be investigated, not silently discarded.

The catalog key is a SHA-256 digest of the absolute source database path. Moving the source database changes its retention/listing group. This key is operational grouping, not a canonical entity identity or a signature.

There is no schedule yet. GUI and CLI provide manual triggers. There is no automatic permanent artwork cleanup.

## Admin GUI

Open **System → Backup** in the sidebar (the existing `/backups` admin route remains valid):

1. Select **Catalog + artwork** or **Scanner inventory**, then **Create backup**. The server snapshots, packages and verifies it in the configured filesystem destination.
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

For scanner inventory, explicitly select `--kind scanner` on all three commands:

```bash
dotnet run --project trackstash-catalog/src/TrackStash.Catalog -- backup \
  --kind scanner --db-path /absolute/path/trackstash-scan.db \
  --backup-root /absolute/path/backups --output json

dotnet run --project trackstash-catalog/src/TrackStash.Catalog -- verify-backup \
  --kind scanner --file /absolute/path/backups/trackstash-scanner-TIMESTAMP-ID.zip \
  --backup-root /absolute/path/backups --output json

dotnet run --project trackstash-catalog/src/TrackStash.Catalog -- restore-backup \
  --kind scanner --file /absolute/path/backups/trackstash-scanner-TIMESTAMP-ID.zip \
  --destination /absolute/path/restored-inventory \
  --backup-root /absolute/path/backups --output json
```

Scanner creation resolves its source from `--db-path`, then `TRACKSTASH_SCAN_DATABASE_PATH`; it never falls back to canonical catalog configuration. `--kind` defaults to `catalog` for compatibility and rejects other values. Verification/restore reject an archive of the wrong selected type.

Restore requires a **new destination directory with an existing parent**, outside Git. It validates all paths, exact inventory, declared sizes, checksums, SQLite integrity/foreign keys, schema and any artwork inventory, extracts to private sibling staging, then publishes the new directory. It never replaces existing data. Catalog restore produces `catalog.db` plus `artwork/`; scanner restore produces only `inventory.db`. Only after reviewing the restore, stop and reconfigure the relevant API/scanner to use the restored paths. Recorded media locations are preserved unchanged, not relocated or checked for accessibility during restore.

Verification/restore accept format 1 (catalog schema 6 or 7, or the scanner contract above), at most 100,000 manifest files, a 16 MiB manifest and 100 GiB declared uncompressed data. Streamed extraction enforces declared per-file limits. Archive checksums detect damage, not malicious authorship; only restore trusted backups. No GUI restore-over-live operation is exposed.

## Consistency and failure boundaries

SQLite's online backup API produces each database snapshot, including committed WAL content; normal catalog/scanner writers need not be stopped (SQLite may briefly contend for locks). The two databases are backed up independently, not as one atomic cross-database snapshot. For catalog backups, snapshot metadata determines which immutable artwork files are copied. Files committed after that snapshot are not required by it. This relies on current immutable/no-delete asset storage and the absence of permanent artwork GC; future GC must coordinate with backup leases/snapshots.

Database integrity, foreign keys, every original's catalog checksum/length, and all archived file checksums are checked before publication. Scanner foreign-key violations produce warnings as described above; catalog foreign-key violations fail. Thumbnails are retained as bytes and checksum-verified, not independently image-decoded. Physical database corruption, missing originals and checksum mismatch fail the operation. Ordinary failures remove only their own staging paths and do not prune older backups.

Completed archives are published by a non-overwriting rename within the destination filesystem. Process/power loss may leave `.work-<id>` or `.restore-<id>` staging directories; these are not completed backups and are not automatically deleted by another process. Investigate/remove only known abandoned staging with no operation running. SQLite snapshot calls are synchronous; cancellation is checked around that call and during subsequent streaming work.

Saving/downloading locally is not proof of a NAS backup. Use a configured NAS destination or a separately verified transfer. Backups and restores are not performed on live user data by the test suites.
