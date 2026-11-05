# TrackStash Catalog Admin

React-admin label maintenance application using React, TypeScript, and Vite. Its data provider translates the public label API into React-admin's resource contract; no database paths, SQL, or provider payload editing belong in the browser.

## Build and Use

Requirements for building: Node.js 22.12 or newer and npm. The API requires .NET 8 and an existing canonical catalog migrated to version 7 or later.

From this directory:

```bash
npm ci
npm run build
dotnet build ../TrackStash.Catalog.Api/TrackStash.Catalog.Api.csproj
```

Configure and start the API following the [module instructions](../../README.md#running-the-api-locally). Open <http://localhost:5187/admin/> and enter the same API key configured on the server.

Build the frontend **before** building/publishing the API. The API project copies the resulting assets to its output/publish directory under `wwwroot/admin`. A clean API-only build is still supported but does not include a GUI. Node/npm are not required to run an already-built/published API with these assets; creation of release installers remains future work.

The app supports:

- **Backup type selection** separates Catalog + artwork from Scanner inventory. Scanner backup requires the server's `CatalogApi__ScannerDatabasePath` or `TRACKSTASH_SCAN_DATABASE_PATH`; without it, the option is disabled with configuration guidance. Scanner ZIPs contain the entire scanner database but no audio files or external sidecars. Retention and saved lists are separate per source/type.

- **System → Backup** in the sidebar: create a verified timestamped catalog/artwork ZIP on the server, follow progress, refresh saved archives and download the same ZIP. The newest 10 verified backups per catalog are retained. Save File-capable browsers stream downloads; other browsers have a 128 MiB in-memory limit. See [backup operations](../../docs/catalog-backups.md). No GUI restore or arbitrary browser-supplied server paths. The original `/backups` admin URL still works; an empty list is normal before the first backup. Restart the API after updating its backend, not just rebuilding the GUI.

- Search canonical names/aliases, sort by name/sort name/ID, and page results (10, 25, 50, or 100).
- Inspect stable IDs, timestamps, aliases, and provider references.
- Label detail has a responsive identity header, preferred logo, biography, country name with a local flag when recognised, external-link icons, and a conditional Favourite toggle. Edit is in the header. The **Details** tab, to the right of Artists, contains the additional artwork gallery, personal notes/review status, and Record details (IDs, aliases, timestamps and link verification sources). These sections do not clutter the other tabs.
- Tabs are ordered **Releases, Artists, Tracks, Details**, with Releases selected initially. Releases, related Artists and Tracks currently show explicit not-yet-available states. Search/filter/sort/pagination on those subpages is future work dependent on backend entity relationships and queries; no mock records or inactive filter controls are shown.
- Stored numeric Beatport label IDs automatically provide header links opening in a new tab. Multiple IDs remain available (preferred first); a curated Beatport label URL suppresses the generated link for the same ID. Generated links use a display-name slug and the provider ID, are not written into the profile, and require no remote fetching.
- Create labels with server-assigned IDs, or edit name/sort name. Clearing sort name sends JSON null.
- In existing-label **Edit**, manage description, country/city, founded year, lifecycle, personal notes, favourite, review status, and typed links with optional verification source URL. Clear optional text/year to remove it; use the link row's remove button to remove a link. Links are displayed as safe HTTP(S) links on detail; descriptions/notes are plain text. Manual saves retain before/after history in storage, not a history-view screen.
- On **Show**, hover/focus the logo to reveal its camera action, or use **Details > Manage artwork** (touch devices also show the camera). The header uses only an explicitly preferred image; choose a preference in the manager if an existing gallery has none. Upload logos/gallery images with mandatory rights text and optional source/attribution. JPEG/PNG only, static, up to 10 MiB/25 megapixels/16384 pixels per side. Uploads save immediately; ordered gallery edits, captions, role/preference and detachment use a separate conditional save. **Replace image** keeps its gallery position and old asset/history. Reload explicitly after a stale edit. Closing the dialog discards only unsaved gallery edits, not completed uploads.
- Artwork and merge-review images use bearer-authenticated fetch and temporary blob URLs, revoked on unmount; no public asset exemption or credentials in image URLs. Originals and thumbnails are stored in the API's configured external managed directory and must be backed up with the database; see [storage configuration](../../README.md#label-artwork-and-galleries).
- Preserve the original detail-response ETag for a pessimistic conditional save. A 412 displays a stale-edit warning and does not retry or overwrite a competing edit; reload before making a fresh edit.
- **Label relationships**, above the related-content tabs, shows parent/imprint and distributor/distributes links with provenance, evidence and notes. **Manage relationships** edits this label's outgoing edges with paged label search, multiple parents/distributors, explicit review confirmation and conditional save. Incoming edges are edited from their originating label. Removing a draft row is not persisted until Save; Cancel makes no changes. Self-links, duplicates and imprint cycles are rejected. A changed owner/target freezes Save until deliberate Reload, which discards drafts/candidate versions and resets confirmation. No dates/territory or automatic evidence fetching yet.
- Review a pairwise duplicate-label merge from survivor detail using **More actions** (the three-dot menu beside Edit) > **Merge label...**: paged name/provider search, combined aliases/provider IDs/release links, blockers, switch-survivor, reason, and explicit confirmation. Opening the menu/action does not merge anything. Cancel does not change identities; refreshing a 15-minute review requires confirmation again. A stale apply is rejected without automatic retry, and successful apply refreshes lists/detail.

Deletion, bulk mutation, automated enrichment, and alias/reference mutation are intentionally unavailable. Source observations have schema-only foundations. No label embedding generation or automatic site fetching runs. Merge galleries retain survivor order, append retired artwork and preserve the winning preference; detached assets remain retained. Merge reviews also show resulting relationship transfers; conflicting provenance, self-links and imprint cycles block apply rather than lose evidence.

## Credentials and Browser Access

The API key is held only in JavaScript memory: not local/session storage, cookies, URL parameters, bundled environment variables, or committed files. Page reloads and explicit logout require signing in again. React-admin preferences also use a memory store, and framework telemetry is disabled.

The unauthenticated `/admin` shell and assets contain no key or catalog data. All `/api` and OpenAPI requests still require the bearer key. API and UI remain loopback-only; this is not a remote/multi-user deployment.

The built GUI uses the same origin as the API. For hot-reload development, configure the API with an explicit extra loopback origin before starting it:

```bash
export CatalogApi__AllowedOrigins__0="http://localhost:5173"
# Set CatalogApi__DatabasePath and CatalogApi__ApiKey separately, then start the API.
```

In another terminal, from this directory:

```bash
npm run dev
```

Open <http://localhost:5173/admin/>. Vite binds localhost only, uses strict port 5173, and proxies `/api` to localhost:5187. If the API uses another port, set `CatalogApi__Port` to the same value in the Vite shell. The API checks the browser's original Origin, even through this development proxy; it does not trust forwarded headers or disable authentication. Direct configured-origin access allows only GET/POST/PATCH and Authorization/Content-Type/If-Match, exposing ETag/Location without cookie credentials.

Do not put the key into `VITE_*` variables: Vite embeds those variables in public JavaScript.

## Verification

```bash
npm run lint
npm test
npm run build
npx playwright install chromium   # first browser test run only
npm run test:e2e
ADMIN_TEST_DEV=1 npm run test:e2e  # same tests through the Vite development proxy
```

Unit tests cover transport mapping, original-token preservation, rejected writes, merge review/apply, errors, and authentication. Browser tests start a temporary catalog/API and check real sign-in, detail/child display, create/edit, stale writes, searching/paging/sorting, logout/refresh, merge cancellation/confirmation/provider preservation, and stale merge rejection. They require the repository's bootstrap module and `sqlite3`, use ports 5199 (API) and optionally 5173 (Vite), and stop their servers/remove their temporary catalog on exit. Never use a live catalog for these tests.

## Dependency and Generated-file Policy

Country/territory names and flags come from the locally installed MIT-licensed `flag-icons` package; only recognised ISO entries are displayed. Unknown stored country codes remain text without inferred flags. Selected music-service SVG paths come from CC0 Simple Icons. Resident Advisor and social links currently use generic icons pending brand-asset review, while still retaining service-specific accessible names. MUI provides interface/fallback icons. All images/icons are local, apart from authenticated catalog artwork fetches; visiting a label never fetches remote site logos. Sources and flag licence text are distributed in `/admin/THIRD-PARTY-NOTICES.txt` (see [source notices](public/THIRD-PARTY-NOTICES.txt)).

Commit source, configuration, `package.json`, and `package-lock.json`; restore with `npm ci`. Do not commit `node_modules`, `dist`, local environment files, or test output. The framework is restored from npm, not copied into source control.

React-admin currently brings query-string 7, whose decoder dependency has a security advisory. The manifest overrides it to patched decode-uri-component 0.5.1 or later. Because the patch is ESM while query-string expects a CommonJS function, [the small compatibility adapter](tools/uri-decoder.cjs) exports its default function via a Vite alias. Both production and development browser suites exercise query parsing/pagination. Remove the override/adapter together when the upstream dependency is upgraded compatibly. No third-party decoder source is vendored.

The initial React-admin production bundle is roughly 1 MB before gzip (about 315 KB compressed), so Vite reports its standard large-chunk advisory. This is not a failed build; bundle splitting can be addressed as screens expand.
