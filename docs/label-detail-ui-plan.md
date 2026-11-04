# Label detail UI plan

Status: Label detail layout and interactions implemented, with frontend unit and production/development browser coverage. Searchable related-content subpages remain future work. This increment improves the existing Label experience before starting Artists; Releases follow once Label and Artist workflows are ready.

## Purpose and visual direction

Use persistent application navigation, a prominent label identity header, compact external links, and a related-content area. Create a Trackstash design using the existing React-admin/MUI infrastructure. The user-supplied mockups show the intended hierarchy for the header, tabbed lists, filter toolbar, sorting and pagination; they are layout references, not a specification for branding, commerce or playback.

There is no Featured section. Tracks and Releases are future label content views, followed by an Artists list. Do not invent data, popularity rankings, playback, or purchase actions.

## Layout

```text
+-----------------------------------------------------------------------+
| Existing Trackstash application bar                                   |
+------------------+----------------------------------------------------+
|                  | [Label logo]  Label name               [Edit] [...] |
| Trackstash       |               [Favourite] [Website] [Discogs] ...   |
| navigation       |               [flag] Country / City / Founded year |
|                  |                                                    |
| Space for future | Description / biography                             |
| areas            |                                                    |
|                  | Releases | Artists | Tracks | Details               |
|                  | Related-content area (not implemented yet)          |
|                  |                                                    |
|                  | Details tab: gallery, notes and record details       |
+------------------+----------------------------------------------------+
```

- Keep the existing top application bar; its future navigation purpose is undecided. Do not add speculative links.
- Retain the left navigation area for other Trackstash modules as they become available. Do not add nonfunctional destinations.
- Put the main logo on the left of the identity header, with name, favourite action, external links and concise location/lifecycle information to its right.
- Put the description below the logo/header block, not in a narrow side column. Preserve paragraphs and render it as plain text.
- Do not add a Top Ten sidebar.
- Use responsive layout: stack the header on narrow screens, wrap actions and links, and retain access to application navigation without horizontal overflow.

## Identity, artwork and actions

### Logo and gallery

- Use the explicitly preferred gallery image for the header. If none is preferred, display an intentional placeholder with an add-artwork action; do not silently select the first image or mutate gallery preference.
- Preserve image proportions and allow space for square logos without cropping important content.
- Reveal a camera or pencil action over the logo on hover and keyboard focus. Keep an equivalent visible/tappable control on touch devices; access must not depend on hover.
- Open the existing artwork manager from that action, including when there is no image.
- Keep the additional gallery in the Details tab's Artwork section, with a clearly labelled Manage artwork action. Do not repeat the gallery beneath the other tabs.
- Reuse authenticated blob fetching, upload validation, provenance/rights fields, replacement, ordering, detachment and preference handling. Do not introduce public image URLs or a second upload workflow.
- Continue to distinguish immediate upload/replacement saves from unsaved gallery edits. Missing or damaged files must show an explicit error and leave management accessible.

### Edit and favourite

- Provide an obvious Edit button in the header for label information. Reuse the current edit workflow initially; organise it into clear sections if needed without changing persistence semantics.
- Place a More actions menu beside Edit at the top right of the header, displayed with a vertical three-dot icon and an accessible name. Include a clearly labelled "Merge label..." action; do not bury the merge entry point in Record details.
- Selecting "Merge label..." opens the existing reviewed merge workflow: choose the other label, preview the result, then explicitly confirm. Opening the menu or selecting the action must never apply a merge directly.
- Favourite corresponds to the existing `profile.favourite` value, not a new following/subscription concept. Present a heart toggle with an accessible label and pressed state.
- Update favourite through the existing conditional label update contract. Preserve the complete current profile, links and unrelated fields; do not send a partial nested profile replacement.
- Use the current aggregate ETag, prevent duplicate submissions, and handle stale updates explicitly with reload/review rather than silently overwriting or retrying.
- Refresh the page record and gallery version state after mutations so subsequent artwork, profile and merge operations use current versions.

### External links

- Display compact service icons to the right of the main logo, using existing curated web links.
- Support recognised services such as the official website, Discogs, MusicBrainz, Bandcamp, Resident Advisor and social profiles. Use a generic link icon for unknown services.
- Give each link a tooltip and accessible service/title label; distinguish multiple links for the same service.
- Use locally bundled, appropriately licensed icons. Verify brand-asset terms and keep required notices; use a generic icon where a suitable asset is unavailable.
- Retain safe HTTP(S) URL validation and safe external-window behaviour. Do not fetch remote logos, scrape pages, or infer unverified links. A deterministic provider-label link derived from a stored numeric provider ID is permitted: render it without persisting it as a curated profile link, open it in a new tab, retain multiple distinct IDs with the preferred ID first, and avoid duplicating a curated label URL for the same ID.
- Implementation bundles selected music-service paths from Simple Icons and uses generic icons for services awaiting brand-asset review. Country names/flags come from `flag-icons`; interface icons come from MUI. Asset sources and licence notices ship with the frontend; see the [admin documentation](../src/TrackStash.Catalog.Admin/README.md#dependency-and-generated-file-policy).

## Country flag and location

- Show a tiny locally bundled national flag beside the country name, keyed by the existing two-letter `profile.countryCode`.
- Keep the country name visible; a flag is supplementary, not the sole representation of location. For example: `[flag] Spain / Barcelona / Founded 2011`.
- Use a maintained ISO country/territory mapping and appropriately licensed flag assets. No remote image dependency and no platform-dependent flag emoji.
- The current form checks only two-letter format, not ISO membership. Unknown or unsupported codes must remain visible as text without a guessed flag or country name; do not rewrite stored values.
- Omit the flag for missing/unresolved countries. Never infer nationality from city, label name, language or provider.
- Mark the flag decorative when the adjacent country name supplies the meaning. Keep small images crisp without stretching.
- Omit absent city/year values and dangling separators. Keep lifecycle and review status understandable without relying on colour.

## Related content and secondary information

- Show Releases, Artists, Tracks and Details tabs in that order, with Releases selected initially. Until the corresponding catalog workflows and associations exist, the first three show clearly labelled unavailable/not-implemented states, not empty-library claims or simulated records. Details contains the artwork gallery, personal notes/review status and Record details; these are not displayed below other tab panels.
- No Featured tab, ranking, playback or multi-entity implementation is included.
- Keep personal notes and review status accessible in Details. The header, biography and logo artwork action remain available regardless of the selected tab.
- Move catalog/provider IDs, aliases and timestamps into a clearly discoverable Record details section. Keep the merge entry point in the header's More actions menu, not in this section or a large block above the label information.
- Keep all information currently visible on the Show page accessible after the redesign.
- Relationship editing remains a separate increment. Do not imply that an editable label hierarchy already exists.

### Longer-term searchable subpages

When backend catalog relationships and query infrastructure are available, evolve Tracks, Releases and related Artists into label-scoped list subpages. Keep the identity header and description above the selected list, with a search/filter toolbar, sort selector, result count and pagination. This is future scope, not a requirement to build unsupported controls in the current UI increment.

- Provide text search within each label's related records, rather than a global search presented as label-scoped.
- Offer filters appropriate to each entity and supported by reliable stored data. Candidate Track filters include release date, BPM range, genre/subgenre, artist/remixer and musical key; Release filters include release date, genre and artist. Define Artist filters once the artist model and label-association semantics exist.
- The mockups illustrate compact filter menus, multi-select choices, Apply and Reset actions, and sorting/pagination alongside the list. Use these patterns where appropriate, with active filters visible and individually removable, and a clear reset-all action.
- Render useful entity-specific rows: artwork and title/name, artist credits where applicable, release date and available musical metadata. Do not add purchase or playback actions as part of this plan.
- Support backend search, filtering, stable sorting and pagination over the full related collection, not client-side filtering of the current page. Define supported fields, combined-filter semantics, missing-value handling and deterministic tie-break ordering in the relevant API contracts.
- Reset pagination when search, filters or sorting change; preserve each subpage's query state in the URL so refresh, browser Back and shared links restore the view.
- Distinguish loading, query errors, no related records and no matches for the current search/filters. Keep filter controls keyboard accessible and usable on narrow screens.
- Add filter-option counts only if the backend supports them with defined semantics; never derive full-collection counts from a page of results.
- Implement these subpages incrementally with their corresponding entity workflows. Do not introduce fake results, nonfunctional search/filter controls or unsupported endpoint assumptions now.

## Delivery steps

1. **Page structure and identity header**
   - Refactor the existing Show layout using MUI components and the current application shell.
   - Add responsive header, preferred artwork/placeholder, biography and secondary detail sections.
   - Preserve direct label URLs and retired-ID behaviour; keep Edit prominent and move the reviewed merge workflow entry point into the header's More actions menu.
2. **Header interactions and location**
   - Wire the existing artwork manager to the logo and visible management action.
   - Add concurrency-safe favourite updates, curated link icons and country-name/flag display.
   - Add only the local assets/data required for these functions, with licensing documented.
3. **Future-content placeholders and form clarity**
   - Add honest Tracks/Releases/Artists placeholders without new entity APIs.
   - Improve directly related edit-form grouping and save messaging where necessary.
4. **Validation and review**
   - Run focused frontend tests, type/build checks, lint and browser tests.
   - Review desktop and narrow-screen rendering against the supplied reference and this plan.
   - Keep live validation read-only unless actual label edits are explicitly requested.

## Acceptance checks

- A label with full data has a readable header, logo, description below the header, safe external links, favourite state, location with flag, and an obvious Edit action.
- A minimally populated label still has a useful layout with deliberate placeholders and no broken images, guessed data or meaningless separators.
- Country mapping handles recognised, missing and unsupported two-letter values without changing stored data.
- Artwork controls work with mouse, keyboard and touch, before and after the first image is uploaded. Gallery management remains discoverable without hovering.
- Favourite updates preserve all other profile fields and links; stale writes surface a conflict and do not overwrite newer edits. Subsequent gallery actions use refreshed versions.
- Authenticated images remain protected; loading, unavailable content and API errors are visible.
- Existing artwork upload/replacement/preference/detachment and merge preview/apply browser coverage continues to pass after actions move.
- The header's More actions menu exposes "Merge label..." by mouse, keyboard and touch; selection opens the reviewed workflow without applying a merge, and explicit preview/confirmation remains required.
- Notes, aliases, provider references, statuses and timestamps remain accessible; future content is explicitly marked as unimplemented.
- Keyboard focus, accessible names, contrast, long label names/descriptions, multiple external links and narrow screens are covered.
- No schema migration, automatic enrichment, Artist/Release implementation, remote flag/icon requests or live catalog mutation is required by this UI increment.

## Related plans

- [Catalog API design](api-design.md)
- [Shared artwork design](../../trackstash-core/docs/artwork-design.md)
- [Catalog TODO](../TODO.md)
