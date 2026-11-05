# Label Groups and Families

Status: **implemented in migration 8, application/API and admin GUI**. This document records the agreed scope and contracts. It does not authorise a live migration or populate researched relationships; deployment requires a verified backup and separate approval.

## 1. Scope and meaning

Keep three different concepts separate:

| Concept | Meaning | Example |
| --- | --- | --- |
| Label | A canonical release-label identity | Anjunadeep |
| Family/brand group | An umbrella identity under which distinct labels are presented together | Anjuna |
| Organisation group | A business/operating organisation associated with labels | Involved Group |

User decisions: support distinct **family and organisation groups**; start with **flat label-to-group membership**, not nested groups.

A group is not automatically a label, release credit, artist, distributor or legal owner. Use one `label_group` entity with a required `kind` of `family` or `organisation`, not fabricated parent labels and not two independent implementations. Kind is fixed at creation initially; a correction needs a reviewed identity workflow rather than silently reinterpreting existing memberships.

Names are not unique identity keys. An organisation and a release label can legitimately have the same name while being different entities. Do not equate entities using matching names, websites, artists, catalogue prefixes or provider IDs.

Membership means only **documented affiliation with this family or organisation**. It does not mean legal ownership, control, distribution, exclusivity or that all members are imprints of one another. Those stronger claims require separately designed typed relationships and evidence.

### Anjuna example

Conceptually:

```text
Anjuna [family]
  members: Anjunabeats, Anjunadeep, Anjunachill

Involved Group [organisation]
  members: Anjunabeats, Anjunadeep, Anjunachill
```

These are two independent sets of sourced membership assertions. There is **no stored Anjuna-to-Involved Group link** in this increment, and no inferred corporate ownership chart. The existing historical imprint assertion between Anjunadeep and Anjunabeats remains separate.

Reflections becoming Anjunachill is a name-history/identity-continuity question, not group membership. Do not create two label identities or merge existing records automatically to represent that rebrand.

Research starting points, to review when entering each assertion:

- [Involved Group's official label listing](https://www.involved-group.com/).
- [Anjunachill launch/family reporting](https://ozedm.com/above-beyond-launch-new-label-anjunachill/).
- [Anjunadeep's own history](https://anjunadeep.com/us/about).
- [Reflections/Anjunachill rebrand reporting](https://djmag.com/news/above-beyond-relaunch-ambient-and-downtempo-label-anjunachill).

Do not treat a current website listing as proof of the affiliation's start date.

## 2. Membership and history

Labels may belong to multiple families and organisations. Groups may contain any number of labels; use paged queries, not a 100-member group limit or whole-collection replacement.

One stable membership record per `(label_id, group_id)` in the first increment. It is the curated summary of the latest known membership period, not a complete corporate-history timeline:

| Field | Contract |
| --- | --- |
| `id` | Server-assigned stable GUID |
| `labelId`, `groupId` | Existing active endpoints; immutable for an existing membership |
| `status` | Required `current`, `former` or `unknown`; explicitly reviewed, not defaulted to current |
| `since`, `until` | Nullable partial dates: `YYYY`, `YYYY-MM` or `YYYY-MM-DD` |
| `source` | Required nonblank attribution, at most 200 characters |
| `sourceUrl` | Optional absolute HTTP(S) evidence URL, at most 2,048 characters, no credentials |
| `notes` | Optional plain text, at most 2,000 characters |
| `createdUtc`, `updatedUtc`, `version` | Server-owned timestamps and opaque edit token |

Partial dates preserve actual precision: never turn “2005” into an invented January 1 date. Validate Gregorian dates and years 0001–9999, without accepting date-times. Compare the earliest possible `since` with the latest possible `until`; reject a definitely reversed range, while retaining uncertain ordering at coarse precision. Display the entered precision.

Status is the accepted assertion, independent of the current clock. An absent `until` is **not proof of current membership**. `current` cannot have an `until`; `former` may have an unknown end date; `unknown` is explicitly uncertain even if dates exist. Future dates/announced changes are outside the first increment: do not activate or expire memberships automatically. Reject dates definitely in the future relative to the UTC calendar date; an interval containing today remains imprecise rather than a scheduled change.

Sources may disagree. Do not silently coalesce conflicting assertions or store several contradictory active memberships for the same pair. A curator must reconcile the summary, recording uncertainty and competing evidence in notes. Multi-source structured observations remain the future enrichment workflow.

**Rejoining:** update the same record to the latest reviewed status/period, preserving the previous summary in immutable history. A confirmed return is `current`, with the return date as `since` (or null if unknown), not the original joining date; clear `until`. Require explicit confirmation when replacing a former period and retain prior-period evidence in history/notes. Do not invent uninterrupted membership, or misclassify a known current affiliation as unknown just because several periods exist. The editor labels dates as **latest known membership period**. Structured multi-period queries/import remain deferred: revisions describe recorded changes, not a complete effective-date timeline, and earlier periods discovered retrospectively belong in notes until that model exists.

Every create/edit/remove writes immutable full before/after history, with original endpoint IDs, time and entry-method attribution. History survives membership removal and label merges. `manual` is method attribution, not proof of an individually authenticated reviewer. Removal means “remove/correct this assertion”; ordinary departure is an edit to **former**, not deletion.

## 3. Derived related labels

Do not persist sibling/fellow-member edges.

- **Sibling imprints:** distinct labels sharing an explicit parent-label `imprint_of` edge.
- **Same family:** distinct labels with `current` membership in the same `family` group.
- **Same organisation:** distinct labels with `current` membership in the same `organisation` group.

These are different sections/reasons, not one ambiguous “sibling” relation. Multiple reasons are grouped per related label, with the shared parent/group named and linked. Exclude the viewed label, deduplicate results by canonical label ID before pagination, and preserve every supporting reason. No transitive family inference, membership inheritance through imprints, or nested-group traversal.

The current imprint edges have no temporal status. Call those results **shares a recorded parent**, not certified current/historical siblings. Do not imply that a shared organisation establishes a shared parent label.

Default group lists show current members; former and unknown memberships have explicit filters and counts. Former/unknown membership must not silently appear in the current related-label section. Historical results make no claim that two labels were affiliated simultaneously: overlapping partial dates may be uncertain.

Group/parent renames should appear on the next read. Derived lists are query results, not persisted copies or client-side expansion of several paged member lists.

## 4. Persistence and shared contracts

Proposed new tables:

- `label_group`: GUID key, kind, name, shared-normalized name, optional sort name, optional description (12,000 characters), optional official HTTP(S) website, server timestamps and opaque version.
- `label_group_membership`: GUID key, foreign keys to label/group, unique endpoint pair, status, partial dates, evidence/notes, timestamps and opaque version.
- `label_group_revision`: append-only group create/edit snapshots.
- `label_group_membership_revision`: append-only membership snapshots including original IDs and evidence.

History endpoint IDs deliberately remain historical values, not foreign keys that follow merges or disappear on removal. Protect history against update/delete. Live tables use foreign keys without cascading deletion. Index membership by `(group_id,status,label_id)` and `(label_id,status,group_id)`; group queries use stable normalized-name/ID ordering.

Provider-independent repositories belong in core; catalog application services own use-case orchestration. Use existing normalization, URL validation, identity conventions, transactional savepoints, Problem Details, authentication and explicit schema-upgrade patterns. SQLite triggers implement equivalent safeguards for every supported writer, not only HTTP.

No separate group alias/provider-reference/artwork framework in the first delivery. Existing shared artwork can later gain a proper group association; never copy label assets into fabricated group labels. No automatic remote fetching, label embedding changes, artist/release expansion, or generic organisation registry.

### Version scope

- Group create/edit produces a fresh group version.
- Membership create/edit/remove invalidates the member label and group aggregate versions, and produces a fresh membership version for surviving records.
- Name/description edits do not update every member's label token: membership identity/meaning is unchanged, and linked names are hydrated on read. Creation/edit review checks the last-read endpoint versions to prevent accepting evidence against a renamed or otherwise changed endpoint.
- Derived fellow-member sets can change without changing every other member's label token. Serve them as fresh paged, `no-store` reads; do not imply that a label ETag versions an entire derived network.
- Group/membership errors roll back data, revisions and all version changes, even when a caller catches the exception and commits.

## 5. Proposed API

Routes below are implemented under `/api/v1`; all require existing bearer/origin policy. See the [HTTP examples](../src/TrackStash.Catalog.Api/TrackStash.Catalog.Api.http).

| Method | Route | Purpose |
| --- | --- | --- |
| GET / POST | `/label-groups` | Paged search / create group |
| GET / PATCH | `/label-groups/{id}` | Detail / conditional scalar edit |
| GET | `/label-groups/{id}/members` | Paged memberships with label summaries and status filters |
| GET | `/labels/{id}/group-memberships` | Paged memberships with group summaries and kind/status filters |
| POST | `/label-group-memberships` | Create one reviewed membership from either GUI context |
| GET / PATCH / DELETE | `/label-group-memberships/{id}` | Read / conditional metadata replacement / remove one assertion |
| GET | `/labels/{id}/related-labels` | Paged deduplicated labels with explicit shared-parent/family/organisation reasons |

Use the existing page defaults (25; maximum 100), stable sort with ID tie-breaker, and explicit total. Related-label queries filter by reason category; return all matching reasons for each label in the selected page. Endpoint references include ID/name, group kind where applicable, and edit version. List/query shapes and counts are independent of SQLite.

Group creation accepts `kind`, name and optional sort name/description/website. Name limits and normalization match labels; empty optional values use explicit null. No inferred identity matching or automatic group creation from label names.

Membership creation submits both endpoint IDs, **last-read label/group versions**, and the complete metadata in section 2. Existing pair returns 409 with its membership URI; no upsert or blind POST retry. Return 201, Location, membership ETag and fresh endpoint versions.

PATCH requires the original membership `If-Match`, both last-read endpoint versions and complete editable metadata. IDs are immutable; omitted metadata is not interpreted as deletion. Return a fresh membership and endpoint versions. Missing preconditions return 428, malformed input 400, stale membership or endpoint 412, missing identity 404, retired label/pair conflict 409. Never automatically retry rejected writes.

DELETE requires the membership's original `If-Match` and an explicit GUI removal confirmation. It checks endpoint liveness and removes only that stable record. Endpoint profile renames alone do not stale removal; a changed membership does. Return 204; clients refresh affected label/group views. Repeat removal returns 404 rather than implying a new operation succeeded.

Group scalar PATCH uses its aggregate ETag and existing field-presence conventions. Membership routes do **not** replace a group's entire member set, nor reuse the outgoing imprint replacement endpoint. Ordinary JSON remains capped at 16 KiB, with explicit 413; use per-edge operations to avoid group-size-dependent request limits.

Retired-label membership/related reads redirect to the survivor; writes reject the retired identity. Missing and retired identities must never produce success-shaped empty collections. Responses remain `no-store`; OpenAPI describes ETags, error shapes and write preconditions.

## 6. GUI

Add **Label groups** as a catalog navigation resource alongside Labels, not under System. List shows kind, name and current member count, with name search, kind filter and pagination.

Group detail shows kind prominently, description, official website and paged **Members** with current/former/unknown filters. Add existing labels through paged name/alias/provider search; group creation remains separate and deliberate.

Label detail shows **Families** and **Organisations** as distinct membership sections, with status/date qualifiers, source/evidence and group links. “Add to group” searches by name/kind. Editing from label detail and group detail opens the **same membership editor and operation**; neither writes a reciprocal edge.

Editor requires explicit status selection, shows date precision/help, evidence and notes, explains affiliation is not ownership, and requires review confirmation before Save. Dates are optional; missing evidence URL is allowed, but source is mandatory. Closing/cancelling makes no changes. Mutating a draft resets confirmation. Stale errors require deliberate reload, discard stale draft/target versions and reset confirmation; no silent overwrite/retry.

Departure action is **Mark as former member**. Assertion removal is a separately labelled action with confirmation, not the normal departure workflow.

Group member names link to label detail. Derived **Shares a recorded parent**, **Same family**, and **Same organisation** views show named reasons and navigate to the supporting parent/group. Preserve the existing label tabs in order: Releases, Artists, Tracks, Details, then Relationships. The Relationships tab contains imprint/distributor relationships, group memberships and the derived related-label action instead of displaying them on the main label page. Avoid loading all members or duplicating large derived lists above those tabs; use a compact summary with a paged dialog/subpage.

Adding imprint edges from a parent page is a separate improvement to the existing relationship editor; it must write the same child-to-parent edge, not introduce group membership or a redundant `parent_of` row.

## 7. Merge, deletion and operational safety

Label merge previews must include memberships before apply is enabled against the new schema:

- Transfer memberships from retired label to survivor, preserving group IDs and evidence.
- For the same group, coalesce only when status, dates, source, URL and notes match exactly. Prefer the survivor record's identity/creation time; keep both originals in audit/history.
- Any difference blocks apply for explicit reconciliation; never silently OR current/former status, widen dates or discard provenance.
- Hash relevant membership rows and group identity/version into the reviewed plan; version the plan format so old reviews are rejected as stale.
- Record all transfers/coalescing in permanent history/audit; invalidate affected group versions atomically. A failed transfer must roll back everything.

No group merge, delete or identity retirement UI initially. Do not expose a half-implemented destructive command. Core deletion analysis must recognise memberships as blockers for label/group deletion, and deletion/restore/import paths must not bypass history or resurrect retired labels. Group duplicate resolution requires a later reviewed merge design; same-name groups are not automatically merged.

Forward migration 8 leaves migrations 1–7 unchanged. It creates empty group/membership/history tables and safeguards without guessing groups, memberships or dates from existing data, preserving existing labels, aliases, edges, artwork and tokens. Catalog backup validation accepts schemas 6, 7 and 8; restore keeps the archived schema. No live upgrade without a verified backup and explicit approval.

The existing snapshot approach includes new tables automatically, but catalog backup/manifest schema allowlists, readiness and fixtures require an explicit update for the new version. Preserve schema 6/7 archive verification/restore and test new group/history round trips. Do not change scanner schemas or media locations.

## 8. Delivery and acceptance

1. Core membership/group contracts, graph-independent validation, migration, history and version safeguards.
2. Label merge transfer/audit and all-writer delete/retirement/import safeguards; no unsupported partial transfer.
3. Application/API, paged queries and OpenAPI; identical membership operation from either endpoint.
4. Group list/detail and reusable reviewed membership editor; clearly labelled derived related-label views.
5. Backup compatibility, temporary-catalog regression/browser tests and documentation; separately approved live rollout.

Required checks:

- Same-named family/organisation/label remain distinct; no name or website auto-linking.
- Multiple memberships and large groups paginate correctly; no arbitrary 100-member group cap.
- Creating from either endpoint yields one record; duplicates, borrowed IDs and endpoint reassignment fail.
- Current/former/unknown filtering, partial dates, impossible/reversed/future dates and explicit no-clock-driven changes behave as specified.
- Group membership never implies imprint/ownership/distribution; current related sets exclude unknown/former entries.
- Related labels deduplicate before pagination while retaining all supporting groups/parents; no self or inherited/transitive membership.
- Stale membership/endpoint edits do not overwrite; both endpoint token changes/history roll back under injected failures and competing writes.
- Group rename is visible on the next read without mass-version updates; derived sets refresh independently of label ETags.
- Compatible label merges transfer/coalesce; conflicting metadata blocks; membership/group edits stale reviews; retired IDs cannot be reused.
- Departure preserves former membership; removal retains history; safe deletion blockers and no public destructive group operations are verified.
- Existing imprint/distribution behavior and label tab order remain unchanged, including narrow/touch layouts.
- Migration is data-preserving/idempotent; schema 6/7 and new-version archives restore correctly, including retained history.

Deferred independently: nested groups/organisation ownership, multi-period affiliations, former-name history, historical imprint qualifiers, group aliases/provider identities/artwork, group merge/retirement, and automatic enrichment.
