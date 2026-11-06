# Reviewed Purchase-to-Release Catalog Import

Status: **read-only corpus inspector and minimum schema-9 canonical foundations implemented; reviewed import remains design only. No live migration or import has occurred**.

The authenticated API and admin **Purchase releases** screen now provide a paged corpus-flag queue and cached release inspection. Tracks are paged; completeness diagnostics and the deterministic source digest cover the whole release snapshot. Label matches are candidates, not accepted bindings. Missing/null/malformed or contradictory cache observations are explicit blockers. No source writes, catalog imports, network refresh or receipt-accounting ingestion occur. See [configuration and routes](../README.md#next-reviewed-purchase-to-release-import).

Initial inspection bounds: pageSize 1-100, search up to 200 characters, at most 2,000 manifest tracks, 4 MiB per parsed payload, 32 MiB total hydrated observations and 100 observations per track. Oversized/malformed release payloads produce explicit incomplete diagnostics; excessive aggregate observations or unsupported source schemas return source-unavailable errors. These are inspector limits, not an invented limit on future canonical release size. The source digest is an inspection fingerprint, not an immutable review token or apply authorization.

This design replaces a wholesale artist-corpus import with a release-centred workflow. A purchase supplies an entry point; reviewed provider data supplies the label, complete release, track placements, recordings and individual artist credits. Media ownership and file resolution remain separate later work.

## 1. Evidence inspected

Read-only examination on 5 November 2026 of the configured scanner inventory, its raw cached Beatport JSON, derived `bp_*`/`bpc_*` tables, receipt/expanded-purchase CSV shapes, and the live schema-8 catalog. Counts describe this snapshot, not guaranteed future coverage.

| Source | Observed content |
| --- | --- |
| `bpc_release` | 2,198 releases; 439 flagged `in_purchase_corpus` |
| `bpc_track` | 6,128 tracks; 782 flagged `in_purchase_corpus` |
| `bpc_artist` | 8,022 artist IDs; 1,022 flagged `in_purchase_corpus` |
| `bpc_label` | 247 labels; 188 flagged `in_purchase_corpus` |
| `beatport_release_cache` | 2,119 valid JSON values: 2,093 release objects and 26 JSON nulls |
| Release track manifests | 2,071 release objects contain `tracks`; every element is a track URL, not a full track object |
| `beatport_isrc_lookup` | 4,630 successful searches containing 6,128 distinct full track IDs; 49 no-match searches |
| Structured track credits | 1,478 tracks have multiple `artists` objects; 1,585 have nonempty `remixers` |
| Derived credit tables | 7,762 track credits and 16,632 release credits, all role `primary` |
| Canonical catalog | 245 labels; zero artists, releases or recordings |

All 439 purchase-flagged releases' label IDs resolve through existing catalog Beatport references, covering 188 distinct labels. No duplicate Beatport label-reference ownership was found in the catalog snapshot. This is useful evidence, not a replacement for checking all candidates and retired redirects during each import.

### What the raw payloads provide

- Release: `id`, `name`, `label.id/name`, `catalog_number`, `upc`, `publish_date`, `new_release_date`, `track_count`, `artists[]`, `remixers[]`, `tracks[]`, description and image metadata.
- Track: `id`, `name`, `mix_name`, `release.id/name/label`, `artists[]`, `remixers[]`, `isrc`, `length_ms`, BPM, key, genre/subgenre and source dates.
- Artist objects contain individual `id`, `name`, slug and sometimes URL/image metadata. Arrays preserve source credit order. Flattened `Artist`, `ArtistNames`, `AlbumArtist` and `RemixerNames` are display conveniences, not independent identities.
- Release `type` can simply be `{id:1,name:"Release"}`. This does **not** establish whether the release is an album, EP, single or compilation.
- Raw track objects inspected have no explicit disc/track number. Release URL-array order must not silently become official track numbering; some examples list descending track IDs.

### Important limitations

Of 2,071 manifests, **1,137** have every referenced track cached, distinct valid track IDs, matching declared count and matching track-to-release IDs. Within the purchase-flagged subset, **437** have manifests and **178** pass those payload-coverage checks. There are 17,416 track references overall, of which 5,993 have full cached track payloads. These are **coverage candidates**, not import-ready certifications: identity decisions and source consistency still require review. Official order remains unverified but is not an import prerequisite.

The archived [corpus builder](../../_archive/scripts/Build-BeatportCacheEmbeddingCatalog.ps1) reconstructs `bpc_*` from raw release and ISRC caches. It assigns every `artists[]` entry role `primary`, ignores separate `remixers[]`, and derives purchase flags from membership in `bp_track`/`bp_release`. Thus flags are a useful queue hint, **not receipt-line proof of a complete release purchase**.

Raw track payloads contain 1,762 remixer-credit occurrences; **169 distinct remixer IDs are absent from `bpc_artist`**. Import must read raw structured credits, not rely on the derived tables.

The suspicion about compounded artists needs nuance. There are 92 corpus names containing a comma or ` & `, but these include legitimate acts such as `Stoneface & Terminal` (8824). Names such as `Daio Ruan, Gio Correia,Bekman` (1058744) and `Reig (FR), Parus Major` (1055819) also occur as **single structured artist objects in raw release data**. They were not necessarily concatenated by the corpus builder. One provider ID is not proof of one human, and punctuation is not a safe splitter.

No network refresh was performed. Current endpoint availability, authentication, paging/order semantics and permission to retain source data need verification when implementing the provider adapter.

## 2. Agreed scope and boundaries

Private purchase history and receipt-backed queues require the [user identity and authorization foundation](user-identity-design.md) first. Stable users, authenticated actors and owner-scoped private records are separate from shared catalog entities. The current corpus inspector and shared catalog foundations can proceed without implying private purchase accounting or multi-user isolation.

- Select one purchase/release at a time. A track purchase resolves to its containing release.
- Preview and import the **entire release**, including unpurchased tracks.
- Reuse the canonical label when its provider identity resolves unambiguously; review missing/ambiguous labels.
- Add individual release/track artists and remixers from structured evidence, not the corpus en masse.
- Catalog entries may exist without media. Do not create media rows, file matches, acquisition claims, tags, sidecars, file copies or NAS backup work.
- No automatic name-based artist merge, cross-release recording merge, artist collaboration graph, group membership or label ownership inference.
- Keep provider integration bounded to this workflow; do not turn this increment into generic automated enrichment.

Confirmed workflow decisions:

- Complete cached bundles are sufficient for review; provider fetch/refresh is explicit, not required before every import.
- Official track order/numbering is not an import prerequisite. Preserve source sequence for display, and allow manual reordering after import without asserting official numbers.
- Compound source artists may be imported as explicitly unresolved identities and later split into two or more canonical Artists through an evidence-backed reviewed operation.
- Prepare canonical embedding text builders, invalidation and durable work during import; generate vectors asynchronously after commit, with processor activation staged separately.

The contracts below are implementation acceptance criteria, not claims that these operations already exist. Live provider adapter verification and embedding-model activation remain separate operational gates; neither blocks completing this design.

## 3. Relationship model

```text
Purchase source item (private queue/provenance, not ownership of every release track)
  -> provider track -> provider release
  or ----------------> provider release
                           |
                           v
                      Catalog Release -- label link --> Catalog Label
                           |
                           +-- ordered release credits --> Artist identities
                           |
                           +-- Release track placement -- recording --> Recording
                                     |                                  |
                                     +-- listing credits --> Artists    +-- accepted recording credits --> Artists
                                     +-- provider track reference
```

### Artist identity versus credit expression

An Artist represents an accepted performing identity: person, group, project or currently unknown kind. Its GUID is independent of its name and provider IDs. A stage act is not automatically an alias of its members or their legal identities.

A credit preserves the source's credited name, role and order while linking an accepted Artist. The same Artist may hold more than one role on one track. Separately preserve source array/path and raw credit text; do not invent joining punctuation or split flattened display names.

Each raw object is a **source identity candidate**, not unquestionable canonical truth:

1. Resolve all existing provider-reference candidates; follow supported retired redirects.
2. Exact unique active reference suggests reuse, subject to contradictions.
3. No reference: propose a new identity or explicitly link an existing candidate after review. Name similarity only retrieves candidates.
4. For a suspicious compound identity, require an explicit decision: accept a documented single act, accept an unresolved compound identity, resolve separately evidenced contributors, or defer the release. Unresolved compound acceptance does not block the import. Never assign the same compound provider ID to several independent Artists.
5. If separate contributors are accepted, retain the compound provider expression in source-credit provenance rather than pretending its ID individually identifies each contributor. Record reviewer decisions and evidence.

Artist kind defaults to unknown, not person. Producer, composer, vocalist and featured roles require explicit evidence; neither a title's "feat." nor an `artists[]` entry is sufficient to invent those roles.

### Unresolved compound identities and later splitting

Keep identity-review state separate from Artist kind: an unresolved compound is not automatically a group, collaboration act or several people. Initial import stores its stable GUID, original provider identity/name, explicit unresolved status and accepted credit occurrences. Search, detail and import previews expose that status; reuse of the same provider ID must not quietly convert it into a verified act. Legitimate named duos remain single identities unless evidence establishes otherwise.

The first import increment must preserve enough information for a later split, without requiring that split editor to ship first. Design the future operation as follows:

- Review two or more new/existing target Artists and supporting identity evidence. Never split by punctuation alone.
- Preview affected release, placement and Recording credits separately. Choose which occurrences resolve to which targets and roles; do not assume every credited contributor participated in every recording.
- Replace selected compound credit occurrences with ordered target credits, retaining the original credited expression, source observation and before/after lineage in permanent history. Preserve other credits and any occurrences that remain unresolved.
- Keep the original compound provider reference attached to its source identity. Individual targets receive only separately evidenced individual provider references. A future import of the compound ID consults reviewed source-credit resolutions; it must not recreate or blindly expand the compound.
- Retain the original GUID and an explicit split-resolution record linking it to the targets. This is one-to-many resolution, not a merge or a redirect to an arbitrary survivor. Mark it fully resolved only when its relevant occurrences are resolved; otherwise expose partial resolution.
- Apply the split atomically with affected aggregate versions, negative reference-ownership checks, audit and embedding invalidation. Stale plans, contradictory evidence and replay conflicts reject without partial changes. Existing IDs/history remain addressable.

Supporting split provenance and credit-resolution state is a required extension of the Artist foundation. Artist merge/retirement remains a separate future workflow.

### Release

A Release is a specific edition/listing with a complete track manifest. Distinct Beatport release IDs remain distinct release candidates unless deliberately reconciled; equal title/catalog number does not silently merge editions.

Use `release_label_link`, not a second competing primary-label column. Beatport's structured release label proposes the issuing-label association only; it does not establish the label's parent, distributor, group or ownership.

Preserve catalog number, UPC, source dates and their meanings. Missing dates stay unknown; purchase/scan timestamps are not release dates. Retain `publish_date` and `new_release_date` as separate observations until provider date semantics are verified. Do not infer first release, remaster date or release type from a generic provider value.

Release `artists[]` proposes ordered primary credits and `remixers[]` ordered remixer credits **at release scope**. Do not copy every release contributor onto every track or manufacture a release artist by joining all track artists. Empty arrays are explicitly empty source observations, not an instruction to erase curated credits.

### Release track placement

Evolve `release_recording` into a first-class stable placement with its own GUID, a release FK and recording FK. The same recording may appear twice in one release; `(release_id,recording_id)` cannot remain the primary key.

Retain nullable official disc/track numbers separately from mandatory reviewed display order, with order provenance. Default to source-array sequence explicitly labelled **source order / numbering unverified**. Do not invent official numbers by reversing IDs, sorting titles or using ISRC search result order. Allow an evidenced curator correction. A verified complete manifest with explicitly unknown numbering can be imported; it must not masquerade as an officially numbered tracklist.

Allow manual placement reordering after import without requiring official-order evidence. It changes display order only, preserving placement IDs, recording links, credits, source sequence and any independently evidenced official numbers. Mark order provenance as manual; validate a complete permutation of the release's current placement IDs under its concurrency token. Reimport must preserve manual display order unless an explicit reviewed change accepts replacement. Newly discovered placements require a visible reconciliation, not an implicit reset.

Store Beatport track-listing IDs on placement external references, with unique active ownership per `(source,external_id)`. This associates a provider track with its source release and chosen recording, rather than assuming the provider ID alone identifies an audio master.

If future data contradicts that one track ID belongs to one source release, block and inspect; do not automatically attach that ID to multiple placements.

### Recording

Recording is the abstract recording/master, independent of release, format, location and ownership. Remixes, edits and remasters are distinct recordings; multiple placements may refer to one recording.

- Exact previously accepted track-listing binding resolves its placement and recording.
- Existing accepted recording references may supply a candidate; multiple bindings or contradictions block.
- Different track IDs with equal ISRC/title/mix/duration are candidates for shared recording identity, **not an automatic merge**. An ISRC is not a global uniqueness constraint.
- For an unbound track, require acceptance of a proposed new Recording or an evidenced link to an existing one. Never create a duplicate silently when a plausible existing identity is unresolved.
- Preserve title, mix designation and source ISRC/duration. "Original Mix" is version metadata, not proof of original chronology. A remaster title does not establish the source recording or year.
- `remaster_of`/`remix_of` links require explicit source-recording evidence and review; do not infer them from matching titles.

### Credit scope

Preserve release-listing, track-placement and recording credits separately. Provider track `artists[]`/`remixers[]` belong first to the **listing's credits**. On a new Recording, the preview can propose accepting them as recording contributors too. On an existing Recording, show additions/conflicts explicitly; a later release must not overwrite accepted contributor identities or change another release's credited spelling.

Keep role-specific order (one-based within each source array), credited name and evidence. Same Artist in primary and remixer roles is valid. Preserve observations of missing roles without inventing contributors. Source-specific spelling belongs in credits/provenance, not automatic canonical name changes.

Artist appearances on label pages derive through releases/placements/credits; do not add a redundant artist-to-label affiliation table.

## 4. Source acquisition and completeness

Introduce a bounded catalog source adapter returning an immutable, provider-neutral release bundle. The scanner adapter reads configured inventory/cache sources **read-only**; public catalog contracts contain no SQL/table names or arbitrary source database paths.

1. Pick a receipt source item or a clearly labelled corpus-flagged candidate. Preserve item type/provider ID and stable opaque source identity privately. Financial values, email IDs and unrelated receipts are not public preview fields.
2. Resolve track-to-release from a full payload or an explicitly validated provider lookup.
3. Read a consistent local source snapshot. Treat missing, malformed, JSON-null and incomplete objects as explicit diagnostics.
4. Read the full release manifest and hydrate **each listed track** by ID. Search-result tracks that merely share an ISRC do not establish release membership.
5. Prefer cached full objects; allow a deliberate provider fetch/refresh to fill gaps. Follow only validated provider IDs/approved endpoints, not arbitrary URLs embedded in cache data. Bound paging, payload sizes, track count and time; honor cancellation and rate limits. Authentication is server-configured, never in previews or artifacts.
6. Require matching provider release/label identity, distinct valid manifest items, and complete pagination/count reconciliation. Source disagreements, missing details or incomplete tracklists block whole-release apply; unknown official order does not.
7. Freeze source payloads, retrieval timestamps, content digests and versioned mappings into the bundle. Network reads occur before the catalog write transaction, never during apply.

Cached data may be old or currently unavailable online. Display fetched-at/source status honestly. A complete cached bundle may be reviewed without network access; a null/partial release must not be success-shaped fallback data. Intentional partial-release cataloging would need a separate future operation, not this complete-release import.

Source provenance stores only the catalog-relevant observations needed to reconstruct the review, subject to provider permissions/retention. URLs to artwork are metadata, not automatic download permission. Shared artwork attachment can follow later under the existing rights/backup rules.

## 5. Review and atomic apply

Suggested UI: **Catalog imports -> Purchase releases**, with a bounded paged queue. Label detail's existing tabs stay uncluttered.

The preview contains:

- Entry-point kind/ID, source timestamps and complete/incomplete status.
- Resolved/new/ambiguous label, release and Artist candidates, with individual provider IDs.
- Every track placement: title/version, source order versus official numbering, proposed/new/reused Recording, individual primary/remixer credits.
- Existing versus proposed scalar fields and credit changes; no silent replacement of manual metadata.
- Explicit identity decisions, warnings and blockers; proposed new/reused counts.
- Separate receipt/corpus provenance: importing all tracks does not expand purchase or media ownership.

Reviewer may cancel, change candidate decisions, obtain missing data, or refresh the preview. All changed decisions invalidate the old review token and reset confirmation. Invalid identities, undecided ambiguous bindings, incomplete source data and conflicting provider bindings prevent Apply. Explicit acceptance of a flagged unresolved compound Artist is a valid decision, not an undecided binding.

Application service applies an immutable plan in one storage transaction:

1. Revalidate plan version, expiry, source bundle digest, decision digest and current versions of **all affected catalog aggregates**.
2. Recheck provider-reference ownership, including previously absent references, within the transaction. Another import creating the same binding must stale/conflict, not duplicate.
3. Create accepted identities, release/label links, placements and credits; attach accepted provider references and observations.
4. Commit all entities, endpoint invalidations, embedding document state/durable work, immutable source/decision/before/after audit and operation outcome together.
5. On failure roll back everything, including history and tokens, using repository-standard transactional/savepoint protection.

Do not replace an existing release's complete graph with a scanner-derived partial graph. Removal of existing placements/credits requires a separately visible reviewed change, never absence from an incomplete payload. Source-name differences are observations; curated names remain unless explicitly accepted.

Use a stable operation ID, immutable plan and strong original review ETag. Replaying an applied operation with the same accepted request returns its existing outcome; different replay intent conflicts. Ordinary source fetch/new preview is not blindly retried as a write.

Reimporting a source release resolves existing references and produces an unchanged/no-op or explicit change preview, not duplicate entities. Cross-operation deduplication relies on atomic provider-reference ownership, not just the review ID.

Implemented inspection surface:

- `GET /api/v1/purchase-release-candidates`: paged configured-source candidates, with explicit derived-corpus provenance. Each item lists `admittedReleaseIds` (catalog Releases owning its Beatport release reference); the page reports `admissionStatusAvailable` (schema 9+) and `hideAdmitted`. `hideAdmitted=true` excludes admitted IDs in the source query before counting/paging and returns 409 `admission_status_unavailable` before schema 9. The Admin GUI expands inspection and review inside the selected row.
- `GET /api/v1/purchase-release-candidates/{releaseId}`: cached corpus release inspection, with paged tracks and whole-bundle diagnostics/digest. The initial queue is corpus-only, not receipt-backed.

Implemented reviewed-import HTTP surface (schema 10; temporary databases only):

- `POST /api/v1/release-import-previews`: source candidate and reviewed choices; returns 201, Location and review ETag.
- `GET /api/v1/release-import-previews/{id}`: frozen preview and outcome.
- `GET /api/v1/release-import-previews/{id}/identities`: paged frozen decisions/candidates.
- `GET /api/v1/release-import-previews/{id}/tracks`: paged frozen track metadata/credits.
- `POST /api/v1/release-import-previews/{id}/apply`: original If-Match, reason and deliberate confirmation.

Preserve existing bearer/origin, safe Problem Details, no-store, request limits and cancellation patterns. Distinguish validation 400, missing 404, conflicting identity/replay 409, stale 412 and missing precondition 428. Acquisition is a bounded job if too slow for a request; incomplete acquisition is never an applied import.

Keep large bundles and plans server-side. Page preview tracks/credits and submit bounded candidate decisions, not the entire release graph in a 16 KiB request. If incremental decision editing is needed, persist it as a versioned draft and generate a new immutable preview after review; never mutate an already-confirmed plan.

### Embedding documents and asynchronous generation

Use accepted canonical metadata, not flattened scanner text or copied scanner vectors. Embeddings retrieve identity candidates; they are never identity proof or authority for automatic merges/splits.

Reuse the shared [embedding contracts](../../trackstash-core/src/TrackStash.Core/Services/EmbeddingOrchestrationContracts.cs), [queue orchestrator](../../trackstash-core/src/TrackStash.Core/Services/EmbeddingOrchestrator.cs) and `embedding_document` storage. These provide abstractions, not a complete catalog embedding implementation: the current [SQLite queue adapter](../../trackstash-core/src/TrackStash.Core.Sqlite/SqliteEmbeddingWorkItemStore.cs) opens its own connection. Do not invoke it inside the import and assume transactional enqueueing. Extend shared storage with transaction-enlisted durable work/document state (or a transaction-enlisted outbox delivered idempotently to the shared queue); choose the simplest supported implementation, not a competing embedding subsystem.

Initial deterministic, versioned text templates:

| Entity | Accepted document content |
| --- | --- |
| Artist | Name, accepted aliases, known kind, identity-review status and accepted public biography when available |
| Release | Title, issuing-label name, accepted release contributors/roles, catalog number and accepted public description |
| Recording | Title, mix/version and accepted Recording contributors/roles; do not borrow unaccepted credits from other placements |

No separate placement vector initially. Sparse Artists may use a name-only document with an explicit unresolved marker where appropriate; do not fabricate biographies or collaborator identities. Label document generation remains a separate task, though accepted label-name changes invalidate dependent Release documents.

- Capture accepted source metadata/provenance and persist canonical document text, template/policy version, content hash and target model identity. Hash deterministic semantic content, excluding volatile retrieval timestamps. Existing vector rows must distinguish the generated hash from the currently desired hash.
- Invalidate only affected content: Artist name/accepted-alias changes affect its document; names used by Release/Recording templates also invalidate those dependents. Label-name changes affect issuing Release documents. Credit edits/splits invalidate affected Release/Recording documents and changed Artist documents. Manual placement order changes need no vector work because these templates do not include track order.
- Enqueue changed entity/model work atomically with accepted import or later canonical edits. No-op imports and unchanged document hashes create no redundant generation. If generation is not yet enabled, retain visible desired document state and pending durable work; do not claim a vector exists.
- A worker reads committed canonical state and generates outside the catalog transaction. Model/template/policy changes require deliberate regeneration; scanner model defaults are not automatic approval of a catalog deployment.
- On completion, verify that entity state, desired hash and model/template still match the job. Never publish obsolete output as current. Deduplication of an active job must not lose a newer revision: reconcile/requeue until the latest desired document is processed, including edits during a lease.
- Expose pending/processing/current/stale/failed states and explicit retry diagnostics. Embedding failures do not undo an accepted import; failure to record its durable work does roll back apply. Exclude stale vectors from current semantic matching until regenerated.
- Exclude private receipt/invoice/email/price data, personal notes and media paths from documents and model requests. Initial model processing is local; any remote provider requires separate explicit approval and data-sharing review.
- Backup/restore and forward migration must preserve desired documents, template versions, generated hashes and durable work. Recover interrupted leases after restore; do not require a model to verify or restore a catalog backup.

## 6. Required core changes

### Implemented minimum foundation (migration 9, temporary databases only)

The shared [foundation contract](../../trackstash-core/src/TrackStash.Core/Storage/CatalogFoundations.cs) and [SQLite implementation](../../trackstash-core/src/TrackStash.Core.Sqlite/SqliteCatalogFoundationRepository.cs) extend the existing entities and placement table:

- Artist/Release/Recording versions, conditional entity and metadata writes, Artist kind/unresolved-compound status, Release catalog number/UPC/separate source dates, and Recording duration.
- Stable Release/Recording credit IDs, roles and repeated occurrences with credited expression, source/path and evidence. Placement credits remain a separate scope.
- Stable placement IDs and tokens, repeated Recording appearances, source/display/official numbering distinction, unique provider track-listing ownership and conditional full-permutation manual reordering.
- New provider-reference ownership guards and all-candidate lookups. Existing ambiguous legacy bindings are preserved and explicitly reported rather than guessed, dropped or silently constrained away.
- Savepoint-protected multi-write operations and permanent before/after foundation operation history. Caller catch-and-commit cannot retain a partially failed entity/credit/placement save.
- Schema 6/7/8/9 catalog backup verification/restore, including tokens, stable placements, credits and history. Restore preserves its archived schema.

The existing model getters expose metadata/versions and enriched credit/placement details. Legacy ID-based upserts remain additive and preserve new scalar metadata; their pair-keyed credits/placements are marked legacy. They must retain enriched `Detail`/`Placement` projections when round-tripping stable records. Losing those details cannot silently duplicate or overwrite a stable credit/placement: it conflicts and rolls back. Legacy replacement/deletion is blocked for foundation history or richer state until lifecycle-safe restore exists. The legacy YAML surface is not the new conditional import API.

Foundation metadata updates are full replacement of the typed metadata fields under an expected token; unknown values are null/default, not silent inferred data. Credit/placement saves are stable-ID upserts and add explicit supplied references/credits; omission does not delete existing observations. Removal/reconciliation must be designed in the immutable reviewed import workflow rather than performed through generic collection replacement.

New shared-foundation operations require schema 9. The existing label/inspector API continues to require at least schema 8; general Artist/Release/Recording HTTP editors remain future work. Do not upgrade the live catalog just to use the inspector. Migration 9 alone does not implement Artist split editing, reviewed import plans, embedding lifecycle, receipt accounting or user authentication. The separately gated import increment below requires schema 10.

### Implemented reviewed import (migration 10, temporary databases only)

Read-only canonical browsing now follows admission: **Releases** in the left menu provides title/catalog-number search and stable paginated results; each label's **Releases** tab queries only releases linked to that label. Release titles and the import outcome's **View release** link open the same detail page. It shows canonical label links, catalog number/UPC/dates, release-level credits, provider identities and paged Recording placements with placement-specific credits, unresolved-compound markers and separate display/source/official numbering. Artist credits link to read-only Artist detail pages; Recording IDs remain unlinked until their own pages exist. Browsing requires schema 9+ and never migrates, edits, infers ownership or accesses media files; schema 8 label/inspector use remains available.

The [shared import contract](../../trackstash-core/src/TrackStash.Core/Storage/ReleaseImports.cs), [SQLite repository](../../trackstash-core/src/TrackStash.Core.Sqlite/SqliteReleaseImportRepository.cs), [application service](../src/TrackStash.Catalog.Application/ReleaseImportService.cs), [HTTP endpoints](../src/TrackStash.Catalog.Api/ReleaseImportEndpoints.cs) and [review UI](../src/TrackStash.Catalog.Admin/src/ReleaseImportReview.tsx) implement:

- Cache-only complete-release acquisition before canonical writes. Full bounded track pages must have identical whole-bundle digests/counts; inconsistent snapshots fail rather than being combined. Frozen plans contain sanitized observations and timestamps, not raw private provider payloads.
- Immutable format-v1 plans with opaque operation ID, SHA-256 review token, 15-minute expiry, source/catalog stamps, explicit evidenced decisions, identity candidates, separate source credit scopes and proposed metadata/counts. Plans stay server-side; HTTP requests remain bounded to one decision plus an optional base-preview ID.
- Decision edits copy the previous choices into a new plan, append permanent supersession, and invalidate the old plan for apply. Creating/reading/cancelling a preview does not create canonical entities. Applied plans/outcomes/provenance are permanent; expired unapplied reviews remain historical records.
- The review UI retains unsaved action/identity/evidence drafts across pages and individual saves, restores saved evidence from the frozen plan, and keeps identity/track page positions and the release-level rationale. **Save all edited decisions** submits only explicitly edited, valid choices in sequence using bounded one-decision requests. Each successful save advances the immutable preview; if a later save fails, successful decisions remain saved and remaining drafts are retained for explicit recovery. Unsaved edits prevent Apply; any edit/save clears final confirmation. A fresh preview deliberately discards drafts; navigating away or reloading also loses in-memory unsaved edits.
- **Bulk accept straightforward identities** explicitly accepts undecided, unbound new Artist/Recording identities across all frozen pages without typed reasons. It excludes compound flags, existing candidates, potentially shared source recordings, and user-edited drafts. A visible standard bulk-acceptance statement supplies decision evidence and the empty overall rationale; it is an operator action, not invented identity evidence. Requests retain bounded immutable supersession and partial-failure draft recovery. Final confirmation and all source/catalog blockers remain mandatory; the button does not apply an import. **Save all edited decisions** submits only explicitly edited, valid choices in sequence using bounded one-decision requests. Each successful save advances the immutable preview; if a later save fails, successful decisions remain saved and remaining drafts are retained for explicit recovery. Unsaved edits prevent Apply; any edit/save clears final confirmation. A fresh preview deliberately discards drafts; navigating away or reloading also loses in-memory unsaved edits.
- Exactly one active source-label match is required. Missing/ambiguous labels must be resolved through existing label maintenance. Unbound Artist and Recording identities require explicit acceptance or evidenced reuse. Punctuation only raises a potential-compound review flag; it never splits identities. Both newly accepted and previously unreviewed bound compounds can be marked unresolved.
- Existing Artist names/aliases and Recording metadata/credits are preserved on reuse. Multiple provider Artist IDs explicitly bound to one canonical Artist are combined into one conditional reference update. Provider track IDs bind placements, not new Recording master references. Different mix/ISRC evidence blocks incompatible Recording reuse.
- Strong original ETag, reason and deliberate confirmation; 400 invalid request, 404 missing review, 409 blocked/conflicting/superseded, 410 expired, 412 stale source/catalog/token, 428 missing precondition. Replay with the same original token/reason returns the saved outcome without needing the source or rewriting history; changed intent conflicts.
- Source is checked again before the catalog transaction. A conservative all-writer catalog stamp is checked inside it, covering identity candidates and negative reference/placement bindings as well as known endpoints. **Any relevant canonical table edit, including an unrelated entity edit, can stale a review** in this initial implementation. This deliberately trades concurrency for a simpler safe first release; finer-grained affected-entity read sets may follow.
- Savepoint-protected atomic creation of entities, release/label links, stable credits/placements, accepted references, source binding, actual foundation snapshots, complete review/apply audit, outcome and transaction-enlisted canonical text/outbox. A caught failure followed by caller commit cannot retain partial entities, tokens, history, provenance, documents or jobs.
- Reimport of identical accepted semantic source content is a no-op even if only retrieval timestamps changed. It preserves curated names, contributor choices and manual display order, and does not enqueue vectors. **Changed or previously unreviewed existing release graphs are shown as blocked review, not applied**: additive/removal/split reconciliation remains a separate operation. A different provider edition may be explicitly created after reviewing same-title candidates.
- Deterministic `catalog-text-v1` Artist/Release/Recording documents, desired/generated hashes, pending/stale/current/failed state and one durable revisioned outbox request per entity. Artist names/accepted aliases invalidate dependent contributor documents; label names invalidate linked Releases; credit/scalar changes invalidate their documents. Position-only placement reordering does not enqueue work. Private receipts, notes and media paths are excluded.

The outbox is enlisted in the catalog transaction rather than using the existing independently connected scanner queue. **No embedding processor or model is activated.** Target-model/policy deployment, committed-state document rebuild, lease/revision publication guards, generation and retry/status UI remain separate delivery work. The import screen honestly reports pending work, not generated vectors.

Schema 10 backup/restore retains plans, supersessions, audit/outcomes, source bindings and pending document work; schema 6-9 archives remain supported. Migration 9-to-10 and failure rollback are tested on populated temporary databases. Neither live database is migrated or imported by this increment, and no user API restart is performed automatically.

The following table records the original gaps and remaining delivery intent; the rows covered above are now implemented, not a second schema proposal.

The [current models](../../trackstash-core/src/TrackStash.Core/Storage/Models.cs) and actual migrations already have Artist, Release, Recording and repositories, but their current upserts are not this reviewed workflow.

| Existing limitation | Required foundation |
| --- | --- |
| Artist/release/recording lack aggregate version tokens | Conditional writes and all-writer invalidation for identities, references, aliases, credits and placements |
| External-reference PK includes entity ID; no global unique ownership | Enumerate all candidates and enforce active provider identity ownership for new reviewed imports; diagnose existing collisions before constraints |
| Release lacks typed catalog number/UPC/date fields | Add minimal provider-independent release metadata and separately sourced date meanings |
| Artist lacks kind/review semantics | Minimal identity/unknown kind, explicit unresolved-compound status and durable source-credit/split provenance; full biography/profile/relationships can follow |
| Release credit PK `(release_id,artist_id)` and no role | Stable credit IDs, role, role-specific order, credited name and provenance |
| Recording credit PK `(recording_id,artist_id)` | Stable credit IDs allowing multiple roles/repeated credited occurrences |
| Placement PK `(release_id,recording_id)` | Stable placement IDs, repeated recording appearances, reviewed order and listing references/credits |
| No reviewed manual placement ordering | Conditional reorder of stable placements; preserve source/official order separately and protect manual order during reimport |
| Shared embedding abstractions lack catalog document lifecycle/transaction-enlisted enqueue | Versioned builders, desired/generated hash state, dependency invalidation and durable work committed with canonical changes; worker runs after commit |
| Recording references historically carry provider track IDs | Preserve existing mappings as evidence; reconcile with new placement bindings explicitly, with no silent reassignment or discarded IDs |
| Generic upserts replace collections | Reviewed per-plan reconciliation that protects curated data and unrelated rows |
| Only Labels have reviewed retirement/merge workflows | Require active-identity guards; block any artist/recording merge UI until its domain-specific transfer/audit/redirect implementation exists |
| Backup validation/readiness has explicit schema allowlists | Forward migration, old archive compatibility, new tables/history round trips and separate approved live rollout |

Prefer extending the existing core entities and placement table over introducing a competing canonical `track` model. Use stable GUIDs and opaque version tokens consistently with current Label APIs; retain legacy IDs unchanged. Schema-only historical documentation with unique normalized names/ISRCs is **not** authority for automatic matching.

Storage contracts and catalog orchestration remain backend-independent. SQLite owns migration/SQL guards; any future adapter must meet the same atomic reference ownership, concurrency, audit and no-partial-apply tests. Do not expose SQLite exceptions or scanner table names as public API requirements.

## 7. First end-to-end pilot

Suggested evidence-rich candidate: **Block House**, Beatport release **325565**, issuing label **LuPS Records (18977)**, artist **F-Project (59140)**.

All four listing IDs are cached:

| Track ID | Version | Primary artist | Remixer |
| --- | --- | --- | --- |
| 1665960 | Original Mix | F-Project | None in source |
| 1665961 | Aeron Aether Remix | F-Project | Aeron Aether (54047) |
| 1665962 | Jacco@Work Remix | F-Project | Jacco@Work (26862) |
| 1665963 | Jerico Remix | F-Project | Jerico (45606) |

The table above is sorted for readability, **not a verified official track order**. The cached release manifest lists the reverse sequence. Retain unverified source order by default; official numbering is not required and display order can be changed manually after import.

This pilot demonstrates one label/release, four distinct versioned Recording proposals, four placements, one primary Artist plus three remixers, and separate release-level remixer credits. Verify actual identity reuse at preview time. It is a corpus-flagged candidate, not a claim of verified receipt coverage or local ownership. No pilot import has occurred.

The native read-only inspector has been verified against this live cached release: four hydrated tracks, separate primary/remixer roles, one active canonical label candidate and a stable source digest across track pages. Both live database SHA-256 hashes were unchanged by inspection. This validates the source inspector only, not a canonical import or recording-identity decision.

After the pilot, cover a multi-primary-artist track, a genuine named duo, a malformed compound credit, a compilation/reissue with shared Recording candidates, and an incomplete manifest before scaling the queue.

## Artist experience roadmap

Agreed 2026-11-05. Stage 1 (manual profile, managed artwork and tabbed shell) is
implemented on schema 12; discovery projections and association summaries below
remain planned. Use the supplied
Beatport screenshots as information-layout references while retaining the
TrackStash theme, accessibility and catalog boundaries; do not reproduce
commerce, playback, Charts, hype/exclusivity or provider-follow controls.

### Target layout

- Persistent responsive header: preferred managed portrait (or explicit
  placeholder), Artist name, Favourite, curated web/social links and a generated
  Beatport Artist link when a numeric external ID is known. Portrait/camera action
  and a separate management action both open the artwork manager, as for Labels.
  Curated matching provider links take precedence; multiple external identities
  remain inspectable without silently picking an arbitrary identity.
- Tabs: **Featured, Tracks, Releases, Associations, Details**, with Featured
  selected initially. Preserve tab/filter/page state in navigation and mount data
  queries only for active content. Keep unresolved-compound warnings visible in
  the identity header; they are not automatically split or treated as people.
- **Featured**: manually maintained biography, expandable when long, followed by
  a bounded responsive latest-release card grid with managed covers, title,
  primary Artist names, linked Labels and dates. Include future-dated catalog
  releases with an **Upcoming** badge; this is not a preorder or ownership claim.
  No biography/cover/provider lookup happens automatically.
- **Tracks**: one row per release placement on which the Artist has an explicit
  placement or Recording credit. Combine applicable credit scopes/roles for that
  row without turning release-level credit into track credit. Preserve repeated
  placements and remix/version identity; show unplaced credited Recordings
  separately with no invented release/date. Show thumbnail, title/mix, linked
  primary Artists/remixers, Label, release/date and supported metadata. Default
  newest-first by release publish date, never import time; unknown dates last,
  deterministic tie ordering. Existing raw credit scopes remain inspectable.
- **Releases**: one row per distinct release the Artist is explicitly part of
  through release, placement or Recording credits, including compilations and
  remixes. Aggregate roles rather than multiplying rows by credits/tracks. Show
  thumbnail, title/primary Artists, linked Labels, publish date and track count.
  Default newest-first; distinguish upcoming and unknown dates.
- **Associations**: sketch a Label summary and columns **Remixed for**,
  **Remixed by**, **Collaborated with**. Initially use clearly marked
  not-yet-implemented sections, not sample identities or misleading zero counts.
  Later results must link to supporting catalog evidence.
- **Details**: IDs, aliases, provider references, identity/review metadata, private
  notes and additional artwork gallery. Biography and public links belong to the
  public-facing profile; notes do not appear in Featured or embedding documents.

### Staged delivery

1. **Artist profile and tabbed shell together.** Add bounded manual biography with
   optional source URL, typed official/provider/social links with provenance,
   Favourite, private notes and review status using Label conventions. Reuse
   shared artwork storage/processing/protected serving for Artist portrait/image
   galleries, immutable rights/source attribution, explicit preference,
   replace/reorder/detach and history. Deliver profile edit forms, persistent
   header, all tabs and biography in Featured. Preserve current credited browsing
   while adapting it to tabs; mark Associations as planned. Keep identity edits,
   merges and compound splitting out of this increment.
2. **Recent discovery and proper list projections.** Add date-aware, server-side
   paged/sorted Artist release and placement queries, aggregated roles and safe
   managed-cover summaries. Deliver latest-release cards and newest-first Tracks
   and Releases with search, pagination, supported role/Label/date filters and
   reset controls. Define partial-date comparisons before adding date filters:
   retain observed date precision rather than presenting invented day values.
   Missing/invalid date metadata must be explicit. Do not sort only the loaded
   page or infer dates from purchase/import timestamps.
3. **Evidence-backed Associations.** Implement Label summaries first, then
   directional remix and collaboration summaries. All results are derived
   catalog appearances, not artist signing, ownership, personal relationships or
   permanent manual relationship edges. Keep queries paginated and make the
   supporting releases/tracks available for inspection.
4. **Infrastructure-dependent controls.** Reserve genre/subgenre, BPM and key
   display slots with explicit unavailable states; enable filters only when
   canonical data, provenance and querying exist.
   Playback, playlists, purchase controls, external provider enrichment and
   Artist split/merge workflows need separate designs. Do not display inactive
   controls that suggest these features already work.

Stage 1 is implemented with Artist-specific profile fields (no Label-only
founded-year or lifecycle fields), permanent profile/gallery history and shared
aggregate concurrency. Featured starts with the biography and an explicit
latest-release placeholder. Tracks/Releases now use server-grouped,
title-ordered appearance projections and independent page state: one row per
credited placement (unplaced Recordings separate), one row per distinct Release.
Roles/spellings are aggregated and focusable evidence icons explain release,
Recording and track-listing credit locations. Release rows include managed-cover
thumbnails and linked Labels. Artist Tracks and Releases now use compact
Beatport-inspired rows: sequential number, cover thumbnail and title/Artist
line, Label context, release date and BPM slots; Tracks also reserve
Genre/subgenre and key slots. Track title/mix and primary/remixer lines reuse
the Release component. Credit roles, spellings and focusable scope icons are
retained in expandable Credit details instead of dedicated columns.
Responses retain the viewed Artist's scoped evidence in `credits` and separately
provide full `contributors`: explicit placement plus Recording credits for
track rows, and release-level credits for release rows. Matching Artist
ID/role/exact credited spelling is condensed without borrowing release-wide
credits for tracks. Release `publishDate` and `newReleaseDate` are read from
the existing catalog; the date column uses publish date, then new-release date
if publish date is absent. Unplaced Recordings have no release date.
Musical metadata remains unavailable rather than inferred; optional frontend
Genre/subgenre, BPM/range and key-name fields are display slots, not new backend
API guarantees.
Backfill, canonical metadata/provenance, migration and working filters are
on hold. No disabled filter, playlist or purchase controls imply functionality.
Raw credit endpoints remain available for inspection.
The biography heading has a pencil for a biography/source-only editor; the
portrait camera replaces the redundant header management link. Favourite uses
a heart, Beatport uses its locally bundled logo, and links/actions have tooltips.
Associations is an honest sketch.
Details contains private notes/review, gallery and identities. Profile drafts
retain their original ETag until save, cancel or deliberate reload; completed
uploads are immediate, unsaved gallery edits are separate. Biography and public
links are not added to `catalog-text-v1` in this increment; private notes remain
excluded. Profile/artwork changes invalidate import reviews without scheduling
text generation, and source reimport preserves both.

The grouping/cover/Label portion of stage 2 is now implemented without a schema
change. Date-aware ordering, latest-release cards and supported filters still
follow to complete the
Featured/list experience; the whole roadmap is not one required implementation
batch. No live migration or data write is authorized by this plan.

### Association and counting rules

- A Label summary considers explicit Artist credits on releases linked to that
  Label, including secondary Label links. **Releases** counts distinct canonical
  Release IDs; **Tracks** counts distinct canonical Recording IDs actually
  credited to the Artist in that Label context; **Appearances** counts distinct
  credited placement IDs. Multiple roles/scopes do not multiply counts. A
  release-only credit can contribute a release but must not claim every track.
  Unplaced Recordings have no inferred Label. Counts across Labels may overlap
  for multi-label releases and are not additive library totals.
- **Remixed for**: the current Artist is a remixer and another Artist has primary
  credit in the same track context. **Remixed by** reverses that direction.
  **Collaborated with** requires shared primary credit on the same track; merely
  appearing on the same compilation or release does not qualify.
- Match canonical Artist IDs, exclude self-pairs, deduplicate scope/role evidence
  and preserve Recording-versus-placement provenance. Before implementation,
  define how those scopes combine for one placement and how unplaced Recordings
  contribute; never compare unrelated track contexts or borrow release-level
  primary credits. Show unresolved identities as unresolved, without expanding
  compound names. Summaries describe credits, not verified real-world remix
  commissions or collaboration relationships.

### Acceptance and rollout gates

- Keyboard/touch-accessible tabs and portrait actions; responsive layout, visible
  empty/errors, stable deep links and original-token conditional edits. Changing
  tabs must not silently discard unsaved profile/gallery drafts.
- Profile/artwork writes preserve original versions, immutable history, rollback
  cleanup and backup/restore; future migrations must preserve existing Artists,
  credits, references, import outcomes and shared Label/Release assets. Deletion
  and legacy replacement stay blocked until richer Artist state can be safely
  snapshotted/restored. Reuse shared helpers rather than copy Label implementations.
- Audit public/private profile fields against text-document and import
  invalidation rules: biography/public links must not introduce private notes,
  overwrite curator data on reimport or create embedding work for image-only
  changes without an explicit text-template decision.
- Test multiple credit roles/scopes, repeated placements, reissues/compilations,
  multi-label links, same-name Artists, unresolved compounds, unplaced Recordings,
  unknown/partial/future dates and stable pagination. Verify remix direction,
  no self-links and no compilation-only collaboration claims.
- Use temporary catalogs and fake provider fixtures for validation. Live
  activation remains separately approved after verified catalog-plus-artwork
  backup and migration rehearsal; no schema upgrade at API startup.

## 8. Delivery and acceptance

Read-only Artist browsing provides a sidebar list with literal name/sort-name/alias search, stable pagination, role filtering, identity details and provider references on schema 9+. Release and placement credits link to the Artist page; the Label's Artists tab derives related Artists through its linked releases and distinguishes primary/remixer roles. This is credit evidence, not a signing/ownership relationship. Artist detail pages show grouped release appearances by role/scope/credited spelling and paged track credits retaining repeated placements, separate Recording/placement scopes and unplaced Recordings. Schema 12 adds the tabbed profile/portrait increment described above. Unresolved compounds remain explicit; no splitting, merging, identity editing or media matching is introduced, and no live migration is performed.

Label-scoped Release browsing shows a tiny managed preferred-cover thumbnail and release-level primary Artist credits instead of repeating the current Label. Collaborating primary credits retain their order/spelling; absent release credits are not inferred from tracks or remixers. The global Release list retains Label links. This listing projection is read-only and does not change import identity decisions.

Release browsing and detail presentation now include managed covers/gallery on schema 11, separate from source import. Manual upload and provider-neutral candidate/explicit attachment reuse shared artwork validation, rights, version/history and backup rules. No live provider adapters, automatic downloads, Label-logo fallback or Recording inheritance are enabled. See [shared artwork design](../../trackstash-core/docs/artwork-design.md) and [artwork API](api-design.md#managed-release-artwork-and-provider-candidates-schema-11). Live migration remains separately gated.

### Release track presentation and shared-list roadmap

The approved presentation-first increment removes redundant header artwork management in favour of the cover/camera action and adds a hinted Beatport logo link when a valid numeric release ID is known. Track rows show sequential presentation numbers across pages, title followed by mix name, primary Artists followed by remixers and duration. Label links remain in the Release header rather than repeating a redundant Label column in the track list. Explicit placement and Recording credits are combined and deduplicated by Artist ID/role/exact credited spelling; release-wide credits never stand in for track credits. Separate credit provenance, ISRC, identities and source/official numbering remain in expandable track details. Presentation numbers do not write or assert official numbering.

Artist Tracks now reuse the title/Artist/remixer component and show Label context plus reserved release-date and musical-metadata slots; Artist Releases use the equivalent numbered cover/title/Artist/Label/date/BPM layout. Unavailable dates, Genre/subgenre, BPM and key remain explicit until a richer response is supplied. Redundant Label/date fields remain omitted within a Release. Metadata infrastructure and backfill are on hold; musical key needs a defined representation before a classic/Camelot preference, and filters require real server-side querying. Tiny waveforms require available media and real analysis. Playlist actions require working catalog playlist operations, not misleading inactive buttons. No additional schema migration, media matching or automatic enrichment is introduced by the presentation increment.

1. Completed: read-only cached bundle inspector and paged corpus-flag queue, with no canonical writes. Expose missing details/order and structured credits. Receipt ingestion and deliberate provider refresh remain separate adapter work.
2. Completed: minimum Artist/Release/Recording/placement/credit/reference/version foundations and migration 9, including unresolved compound state and source-credit expression/evidence for later split resolution, tested without live activation. The actual split-resolution workflow remains future work.
3. Completed: immutable review plans, candidate resolution and atomic idempotent apply with domain-specific audit, embedding text builders/invalidation and transaction-enlisted durable work (schema 10, temporary catalogs only).
4. Deliver one complete release through GUI/API and conditional manual track reordering; label Releases/Artists/Tracks views can then derive real catalog associations with paging. Activate/test the local embedding processor separately after committed documents are verified.
5. Validate backup/migration compatibility and separately approve rollout; proceed to batches only after the cases below pass.

Required tests:

- Multiple individual artist objects remain separate; punctuation in legitimate act names is preserved; compound identities can be explicitly accepted as unresolved without blocking a complete import. They retain provider identity, original credits and split-ready provenance.
- Primary and remixer arrays survive independently at release and placement scope; same Artist in multiple roles is retained. The 169 corpus-missing remixer IDs can still be proposed from raw payloads.
- Missing/null release, missing track payload, count/pagination mismatch, invalid track URL/ID or contradictory release identity blocks apply without partial catalog data.
- Unknown numbering is clearly distinguished from official order; repeated Recording placements in a release are representable.
- Manual reordering preserves stable placements, source sequence, official numbers and credits; duplicate/missing IDs and stale requests reject. Reimport preserves manual order unless replacement is deliberately reviewed.
- Same provider reference never creates duplicate active identities under competing imports; multiple pre-existing candidates block instead of choosing the first.
- Same-name Artists remain separate; different track IDs/shared ISRC require reviewed Recording reuse; remixes/remasters remain distinct.
- Existing Recording credits and other release placements are not overwritten by a reissue's display credits.
- Stale label/artist/release/recording/placement edits, negative-reference races and source refreshes invalidate review; no automatic overwrite/retry.
- Failure injected after any entity/credit/reference/history write rolls back the complete import, even if a caller catches then commits.
- Replaying an applied operation is stable; conflicting replay rejects; importing the same release again yields no-op or reviewed changes.
- Full release import includes unpurchased tracks but creates no media/acquisition/match/sidecar/tag/backup-coverage assertions.
- Forward migration preserves populated legacy entities/references/credits; malformed legacy bindings are reported, not dropped. Archive restore preserves schema and all provenance/history.
- Accepted metadata yields deterministic versioned embedding documents without private data or invented Artist context. Rollback leaves neither documents nor jobs; no-op import creates no redundant work.
- Dependent name/credit changes invalidate the correct documents; concurrent edits during processing cannot publish stale vectors or lose pending revisions. Generation failure is visible without losing the accepted catalog import; backup/restore retains recoverable work.

Before delivering the later split editor, additionally verify: reviewed one-to-many target selection, per-scope/per-occurrence replacement, partial resolution, original provider-reference retention, future-import reconciliation, stable history/IDs, atomic rollback, stale/replay protection and dependent embedding invalidation.

Deferred: the reviewed compound-Artist split editor (initial schema/provenance must support it), wholesale Artist corpus imports, generic enrichment, full artist profiles/artwork/relationship graphs, artist and recording merges, automated cross-ID master reconciliation, purchase accounting, scanner media resolution, file locations and NAS coverage. Fresh provider access and local embedding-model activation have separate verification gates; no remote model deployment is implied.
