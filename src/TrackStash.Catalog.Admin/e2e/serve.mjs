import { execFileSync, spawn } from 'node:child_process'
import { mkdtempSync, readFileSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join, resolve } from 'node:path'

const root = resolve('../../..')
const temporary = mkdtempSync(join(tmpdir(), 'trackstash-admin-e2e-'))
const database = join(temporary, 'catalog.db')
const scannerDatabase = join(temporary, 'scanner.db')
let api
function cleanup() {
  rmSync(temporary, { recursive: true, force: true })
}
try {
  const scannerScript = readFileSync(join(root, 'trackstash-scan/Private/Initialize-TrackstashDatabase.ps1'), 'utf8')
  const scannerSchema = scannerScript.split('$schema = @"')[1]?.split('"@')[0]
  if (!scannerSchema) throw new Error('Scanner schema fixture was not found.')
  execFileSync('sqlite3', [scannerDatabase, scannerSchema])
  execFileSync('sqlite3', [scannerDatabase, `
    INSERT INTO media_file (media_file_id, content_hash, path, format, size_bytes, last_modified_utc, scanned_utc)
      VALUES (1, 'test-inventory-hash', '/unmounted-test-library/track.flac', 'flac', 123, '2026-11-05', '2026-11-05');
    INSERT INTO metadata (media_file_id, artist, title, album) VALUES (1, 'Scanner artist', 'Scanner title', 'Scanner album');
    INSERT INTO acoustic_fingerprint (media_file_id, algorithm, algorithm_version, fingerprint_raw, generation_status, generated_from_path, generated_utc)
      VALUES (1, 'chromaprint', '1', 'test-fingerprint', 'complete', '/unmounted-test-library/track.flac', '2026-11-05');
  `])
  const sourceArtists = [{ id: 4907, name: 'Kasey Taylor' }, { id: 635680, name: 'Amega' }]
  const sourceRemixers = [{ id: 69845, name: 'GMJ' }, { id: 118649, name: 'Matter' }]
  const sourceLabel = { id: 42, name: 'Fixture label' }
  const sourceTracks = Array.from({ length: 26 }, (_, index) => ({
    id: 1001 + index, name: `Inspector track ${index + 1}`, mix_name: index === 0 ? 'GMJ & Matter Remix' : 'Original Mix',
    release: { id: 900, label: sourceLabel }, artists: sourceArtists, remixers: index === 0 ? sourceRemixers : [],
    isrc: `TEST${index}`, length_ms: 123456,
  }))
  const sourceRelease = {
    id: 900, name: 'Inspector complete release', label: sourceLabel, artists: sourceArtists, remixers: sourceRemixers,
    track_count: sourceTracks.length, catalog_number: 'FIX001', upc: null, publish_date: '2020-01-01', new_release_date: '2020-01-02',
    tracks: sourceTracks.map(track => `https://api.beatport.com/v4/catalog/tracks/${track.id}/`),
  }
  const sqlString = value => `'${JSON.stringify(value).replaceAll("'", "''")}'`
  execFileSync('sqlite3', [scannerDatabase, `
    CREATE TABLE IF NOT EXISTS bpc_release (beatport_release_id INTEGER PRIMARY KEY, title TEXT NOT NULL, in_purchase_corpus INTEGER NOT NULL);
    CREATE TABLE IF NOT EXISTS beatport_release_cache (beatport_release_id INTEGER PRIMARY KEY, release_json TEXT NOT NULL, fetched_utc TEXT NOT NULL);
    CREATE TABLE IF NOT EXISTS beatport_isrc_lookup (normalized_isrc TEXT PRIMARY KEY, lookup_status TEXT NOT NULL, result_count INTEGER NOT NULL, tracks_json TEXT NOT NULL, searched_utc TEXT NOT NULL);
    INSERT INTO bpc_release (beatport_release_id, title, in_purchase_corpus) VALUES (900, 'Inspector complete release', 1), (901, 'Inspector missing release', 1);
    INSERT INTO beatport_release_cache (beatport_release_id, release_json, fetched_utc) VALUES (900, ${sqlString(sourceRelease)}, '2026-11-05T00:00:00Z'), (901, 'null', '2026-11-05T00:00:00Z');
    INSERT INTO beatport_isrc_lookup (normalized_isrc, lookup_status, result_count, tracks_json, searched_utc) VALUES ('INSPECTOR', 'Success', 26, ${sqlString(sourceTracks)}, '2026-11-05T00:00:00Z');
  `])
  execFileSync('dotnet', ['run', '--project', join(root, 'trackstash-bootstrap/src/TrackStash.Bootstrap'), '--',
    'migrate', '--provider', 'sqlite', '--db-path', database], { stdio: 'pipe' })
  execFileSync('sqlite3', [database, `
    INSERT INTO label (label_id, name) VALUES ('legacy-test', 'Archive Label');
    INSERT INTO label_alias (label_id, value, is_primary) VALUES ('legacy-test', 'Archive Alias', 1);
    INSERT INTO label_external_ref (label_id, source, external_id) VALUES ('legacy-test', 'beatport', '42');
    INSERT INTO label (label_id, name) VALUES ('merge-survivor', 'Merge Survivor'), ('merge-retired', 'Merge Duplicate');
    INSERT INTO label_external_ref (label_id, source, external_id, is_primary) VALUES
      ('merge-survivor', 'beatport', '10955', 1), ('merge-retired', 'beatport', '254639', 1),
      ('merge-retired', 'beatport', 'verified-third-test-id', 0);
  `])
  api = spawn('dotnet', [join(root, 'trackstash-catalog/src/TrackStash.Catalog.Api/bin/Debug/net8.0/TrackStash.Catalog.Api.dll')], {
    stdio: 'inherit',
    env: { ...process.env, CatalogApi__DatabasePath: database, CatalogApi__Port: '5199', CatalogApi__ArtworkRoot: join(temporary, 'artwork-data'),
      CatalogApi__BackupRoot: join(temporary, 'backups'),
      CatalogApi__ScannerDatabasePath: scannerDatabase,
      CatalogApi__ApiKey: 'test-only-browser-key-not-a-production-secret',
      CatalogApi__AllowedOrigins__0: 'http://localhost:5173' },
  })
  api.on('exit', code => { cleanup(); process.exit(code ?? 0) })
  for (const signal of ['SIGINT', 'SIGTERM']) process.on(signal, () => api.kill('SIGTERM'))
} catch (error) {
  cleanup()
  throw error
}
