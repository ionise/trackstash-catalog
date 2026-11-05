# User Identity Before Purchase History

Status: **design only; no user schema, per-user authentication or purchase history implemented**.

The current loopback API accepts one configured bearer key. This gates access but does not establish a user principal or identify the person making a change. It must not be treated as sufficient ownership enforcement for private purchase history.

## Scope and delivery order

Prepare for more than one user without introducing a hosted service, registration, user-management UI, password storage or general collaboration permissions now.

1. Finish the shared catalog foundations independently of purchase accounting.
2. Implement a minimal stable user identity, authenticated actor context and ownership authorization before storing or exposing receipt-backed purchase history or private purchase-source queues.
3. Start with one explicitly provisioned local user and map the configured local API credential to that user.
4. Introduce external authentication and multi-user administration only when needed, with a separately reviewed deployment/access policy.

The existing corpus inspector is not a private purchase-history API: it exposes derived entry-point flags, not receipt details. Its current shared-key access is a local development arrangement, not a promise of multi-user isolation. Receipt ingestion must not reuse that arrangement without the identity/authorization foundation.

## Minimal proposed schema

These are logical contracts, not executable migration DDL.

### `catalog_user`

- `user_id`: stable canonical GUID primary key; unrelated to Artist identities, email or login names.
- `display_name`: human-readable, not unique and not an authentication key.
- `status`: active or disabled, explicitly managed rather than inferred from activity.
- `version`: opaque concurrency token.
- `created_utc`, `updated_utc`: timestamps.

Do not require an email, password hash or personal profile for a local owner. Provision explicitly through a native administrative operation; do not silently create a default owner at API startup. Disabling a user retains their purchases and history. Hard deletion stays blocked while ownership/audit references exist.

### `user_auth_identity`

- `auth_identity_id`: stable GUID primary key.
- `user_id`: required FK to `catalog_user`.
- `issuer`: configured authentication authority/credential namespace.
- `subject`: stable opaque identity within that issuer.
- `created_utc`: timestamp.
- Unique `(issuer, subject)` binding, with an index on `user_id`.

One user may have several authentication identities. Identity bindings are administered deliberately; do not automatically link users by matching email or display name. An issuer/subject must never authenticate as two users.

The local issuer represents a server-configured credential binding. Its subject is a non-secret credential identifier, not the bearer key or its hash. The secret stays outside the database in existing server secret configuration. Rotation can preserve the same configured credential identity; it must not manufacture a new owner.

For future external login, bind the trusted provider's validated issuer/subject, not an unverified token claim. This table stores associations, not sessions, refresh tokens or credentials. Revocation/session semantics belong to the selected authentication implementation.

### Future purchase ownership

Every future purchase/acquisition aggregate has a required `owner_user_id` FK. Receipt deduplication keys are scoped to owner and purchase provider/account context, not globally unique invoice numbers.

Purchase lines resolve to catalog Releases or track placements when accepted, retaining private purchase-source identity independently of those shared catalog entities. A single-track purchase does not own every track on its release. Source records with unknown ownership must be assigned deliberately before admission; do not infer the owner from Artist, purchaser display text, email matching or whichever user first opens the queue.

Do not add user ownership columns to shared Labels, Artists, Releases, Recordings or placements. Owning a purchase does not imply owning the corresponding shared catalog identity.

## Authentication and authorization

- Authentication establishes an active user and credential identity from server-validated credentials. Never trust a browser-supplied user ID, owner ID, actor field or arbitrary identity header.
- Resolve the local configured key to its provisioned binding and active user. Missing binding, disabled user or invalid credential denies access; no fallback to a default user.
- Pass a typed actor context through API/application services. Do not scatter principal parsing or introduce HTTP dependencies into core storage contracts.
- Query and mutate private purchase aggregates, source queues, exports and jobs only within the authenticated owner's scope. Apply ownership predicates in storage access, not by fetching all users' data and filtering in the browser.
- Initially each user sees only their own private data; no implicit administrator bypass or arbitrary owner selector. Cross-owner access returns a consistent not-found response so object IDs do not reveal private existence.
- Shared catalog editing retains its explicitly documented local-editor policy. Before admitting another user, define shared-catalog permissions rather than assuming every authenticated user is an editor.
- CLI private-data operations must also resolve an explicitly selected, provisioned owner/actor through the native operation's trusted configuration. No unauthenticated default ownership for imports.
- Keep loopback binding, origin checks, bearer secrecy and no-store responses. User identity alone does not make LAN/remote deployment safe or supported.

A password-login/session/OIDC implementation is not chosen now. The stable identity schema must not force one later. Use established authentication components when that deployment is selected rather than implementing cryptography or password handling in catalog code.

## Actor attribution versus ownership

Record the authenticated actor separately from the purchase owner; they are different concepts even when initially the same user.

New reviewed writes should record `actor_user_id` and, where relevant, `auth_identity_id`, alongside operation/source mechanism, timestamp and existing immutable decision/history evidence. Automated work has an explicit system mechanism and originating actor/operation when available; do not create a fictional human user for it.

Historical `manual` actors represent an operation mechanism, not a verified person. Preserve them unchanged and leave historical user attribution explicitly unknown. Do not backfill every existing edit to the first local user or rewrite permanent audit rows. If existing immutable audit formats require an additive attribution structure, preserve their bytes and IDs through a forward migration.

Import previews and applied outcomes for future private purchases are owner-scoped too. Knowing an operation ID must not permit another user to read or replay it.

## Privacy boundaries

- Private receipts, invoices, email identifiers, prices, account details and purchase notes never enter shared catalog metadata or embedding documents/model requests.
- Existing label personal notes/favourites are currently catalog-level metadata, not per-user settings. Before enabling a second user, classify and migrate those fields deliberately; adding users must not silently expose them as shared data.
- Whole-catalog backup ZIPs contain all database owners' private data once purchase history exists. Treat backup creation/listing/download as an explicit operator privilege before multi-user activation; owner-scoped API reads alone are insufficient.
- Keep credentials out of schema, audit, logs, previews and backups. Display non-secret identity identifiers only where needed.

## Migration and acceptance gates

Implement through a forward migration with existing IDs/data preserved and old backup compatibility checked. No automatic live migration or user provisioning; verify a backup, rehearse and obtain separate live approval.

Required tests before private purchase history:

- Local credential establishes the expected active user; missing/disabled bindings deny access.
- Credential rotation preserves user ownership; competing identity bindings cannot claim the same issuer/subject.
- User/owner/actor spoofing in request fields or headers cannot change server-established identity.
- Two temporary users cannot read, mutate, search, export or replay each other's purchases, receipt queues or private operations, including guessed IDs and paged queries.
- Receipt deduplication is stable per owner/provider context without conflating two users' receipts.
- Actor and owner remain distinct in audit; historical unknown actors stay unknown and immutable.
- Concurrent disable/binding changes and stale ownership decisions cannot apply as an incorrectly authorized actor; define transaction-time authorization checks.
- Backup/restore retains users, identity bindings, ownership and attribution without credentials. Multi-user backup access is explicitly restricted before activation.
- Existing single-user catalog flows remain intact; unimplemented remote deployment and registration remain unavailable.

This design is sufficient to sequence the work. Selecting a remote identity provider, roles, registration and deployment topology can wait until multi-user access is actually requested.
