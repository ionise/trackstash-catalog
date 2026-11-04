import { execFileSync, spawn } from 'node:child_process'
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join, resolve } from 'node:path'

const root = resolve('../../..')
const temporary = mkdtempSync(join(tmpdir(), 'trackstash-admin-e2e-'))
const database = join(temporary, 'catalog.db')
let api
function cleanup() {
  rmSync(temporary, { recursive: true, force: true })
}
try {
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
      CatalogApi__ApiKey: 'test-only-browser-key-not-a-production-secret',
      CatalogApi__AllowedOrigins__0: 'http://localhost:5173' },
  })
  api.on('exit', code => { cleanup(); process.exit(code ?? 0) })
  for (const signal of ['SIGINT', 'SIGTERM']) process.on(signal, () => api.kill('SIGTERM'))
} catch (error) {
  cleanup()
  throw error
}
