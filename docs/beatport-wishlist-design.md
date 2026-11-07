# Beatport Wishlist: Design (not implemented)

Status: **proposal for review.** Nothing here is built. It needs a catalog schema migration, so it waits for your go-ahead and a verified backup.

The preferred workflow is now an embedded Beatport shopping/discovery tab on Label and Artist pages, eventually sending selections to a Beatport playlist or basket. Those read-only tabs are implemented; store writes and samples still need capability checks. This local wishlist is optional, not a prerequisite for that workflow. SonoVault, rather than Beatport, is the preferred future enrichment source; any enrichment should first compare candidates and preserve curated data.

## Goal

While browsing Beatport inside the catalog, mark releases you intend to buy. A wished release is **pre-staged**: its Beatport data is cached and its label is matched, so that when you do buy it, the purchase can be admitted (and later linked to media files) with as little review as possible. Later the wishlist could be pushed to a Beatport playlist or cart to make buying easier.

## Where the data lives

| Data | Store | Why |
|------|-------|-----|
| The Beatport release/track JSON | broker tables in the scanner database (already there) | Browsing already caches it; no duplication. |
| "I want this" decisions, notes, status | **new catalog table** (migration 13) | It is your curation, which belongs with the catalog and its backups, not with a cache that can be rebuilt. |

The wishlist does **not** create canonical `release`/`recording` rows. Canonical rows are still created only by the reviewed import, once the purchase exists. This keeps "things I own" and "things I want" separate.

## Proposed migration 13: `beatport-wishlist`

```sql
CREATE TABLE wishlist_release (
  wishlist_id        TEXT PRIMARY KEY,               -- GUID
  source             TEXT NOT NULL DEFAULT 'beatport',
  external_id        TEXT NOT NULL,                  -- Beatport release id
  name               TEXT NOT NULL,                  -- snapshot for display if the cache is cleared
  artists            TEXT,
  label_name         TEXT,
  label_external_id  TEXT,
  catalog_number     TEXT,
  publish_date       TEXT,
  image_url          TEXT,
  status             TEXT NOT NULL DEFAULT 'wanted'  -- wanted | purchased | admitted | dismissed
                     CHECK (status IN ('wanted','purchased','admitted','dismissed')),
  priority           INTEGER NOT NULL DEFAULT 0,
  note               TEXT,
  added_utc          TEXT NOT NULL,
  updated_utc        TEXT NOT NULL,
  purchased_utc      TEXT,                           -- first time it appeared in purchase history
  release_id         TEXT REFERENCES release(release_id) ON DELETE SET NULL, -- set when admitted
  version            TEXT NOT NULL,                  -- ETag for conditional edits, like labels
  UNIQUE (source, external_id)
);
CREATE INDEX ix_wishlist_release_status ON wishlist_release(status, added_utc);
```

Optional and deferred: `wishlist_track` for wanting single tracks rather than whole releases.

## Status lifecycle

```
wanted ──(appears in purchase history)──▶ purchased ──(reviewed import)──▶ admitted
   └──(you remove it)──▶ dismissed (kept so it isn't suggested again; can be restored)
```

Moving to `purchased` and `admitted` is derived and refreshed when the wishlist is read or after an import: purchased if `PurchaseReadinessCache` contains the Beatport id, admitted if a catalog release has `release_external_ref(source='beatport', external_id)`. It is never a manual claim.

## API sketch

- `GET /api/v1/wishlist?status=wanted&q=&page=&pageSize=`
- `POST /api/v1/wishlist` `{ source: "beatport", externalId }` → `201 Created` with a Location header. The server fetches the release through the broker and takes the snapshot itself, so the client can't post a made-up snapshot. Adding a release that's already wished returns the existing entry.
- `PATCH /api/v1/wishlist/{id}` with If-Match: note, priority, or dismissed/wanted.
- `DELETE /api/v1/wishlist/{id}` (hard delete; dismiss is the usual choice).
- Browse results gain `wishlistId`/`wishlistStatus` so the Beatport page can show **Add to wishlist** or **On wishlist**.

## Pre-staging when a release is added

1. Fetch the release and its tracks through the broker (cached as `beatport_broker_entity`, the same data backfill uses).
2. Check that the Beatport label resolves to exactly one catalog label. If not, flag the item **"label needs review"** so you can fix it before buying, rather than during import.
3. When it is purchased, purchase readiness already sees complete source data, so the release appears as **ready** in Purchase releases.

## GUI

- Beatport page: **Add to wishlist** on each release row, and an "On wishlist" chip.
- New **Wishlist** page: filter by status, notes and priority, label-review warnings, links to Beatport and (once admitted) to the catalog release.
- Purchase releases: a "from wishlist" marker.

## Later: pushing to Beatport

Beatport v4 has user playlists under `/v4/my/playlists/` and a cart. Whether the API lets this client write to them needs checking with a live account first (read-only checks, then one test playlist). If it works, add **Send to Beatport playlist** for selected wanted items. Writes would go through the broker, which today only passes through `GET` requests for `/v4/catalog/` and `/v4/my/`, so playlist writes would need a new, narrowly allow-listed broker endpoint.

## Rollout

1. Agree the open questions below.
2. Back up the catalog (the coordinated backup) and rehearse migration 13 on a copy.
3. Implement the API, tests and GUI; apply the migration live only with your approval.

## Open questions for you

1. Whole releases only to start with, or single tracks too?
2. Do you want priority and notes, or just a simple list?
3. When something is bought, should it stay visible as "purchased/admitted" (a history) or drop off the list?
4. Should Bandcamp/Traxsource items share this wishlist later? (The `source` column allows it.)
5. Is a Beatport playlist enough for buying, or is adding to the cart the important part?
