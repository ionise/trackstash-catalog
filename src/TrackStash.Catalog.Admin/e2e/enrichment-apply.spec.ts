import { expect, test } from '@playwright/test'

const key = 'test-only-browser-key-not-a-production-secret'

test('a candidate can be applied directly without a review form or confirmation', async ({ page }) => {
  const id = 'release-recording-0'
  const candidate = {
    providerId: 'sonovault', candidateId: '7', editionId: null, revision: null,
    match: { basis: 'MetadataSearch', explanation: 'Candidate evidence', conflicts: [] },
    metadata: { name: 'Updated title', credits: [] }, evidence: [],
  }
  await page.route(`**/api/v1/recordings/${id}/enrichment/sonovault`, route => route.fulfill({ json: {
    providerId: 'sonovault', retrievedAt: '2026-11-07T00:00:00Z',
    query: { catalogId: id, catalogVersion: 'original-version', name: 'Original title', mixName: null,
      isrc: null, durationMs: null, artistNames: [] }, candidates: [candidate], warnings: [],
  } }))
  await page.route(`**/api/v1/recordings/${id}/enrichment/sonovault/reviews`, route => route.fulfill({
    status: 201, headers: { ETag: '"review-version"' }, json: {
      id: 'review', version: 'review-version', entityId: id, entityKind: 'Recording',
      createdAt: '2026-11-07T00:00:00Z', candidate,
      fields: [{ field: 'Name', label: 'Title', current: 'Original title', proposed: 'Updated title' },
        { field: 'SonoVaultIdentity', label: 'Identity', current: [], proposed: '7' },
        { field: 'PreferredGenre', label: 'Preferred classification', current: null, proposed: ['House'] }],
      warnings: [], outcome: null,
    },
  }))
  let applied = false
  await page.route('**/api/v1/enrichment-reviews/review/apply', route => {
    expect(route.request().headers()['if-match']).toBe('"review-version"')
    expect(route.request().postDataJSON()).toEqual({
      fields: ['Name', 'SonoVaultIdentity'], preferredGenre: null,
      reason: 'Selected SonoVault candidate in the catalog.', confirmed: true,
    })
    applied = true
    return route.fulfill({ json: { reviewId: 'review', entityId: id, appliedAt: '2026-11-07T01:00:00Z',
      fields: ['Name', 'SonoVaultIdentity'] } })
  })
  await page.goto(`/admin/#/recordings/${id}/show`)
  await page.getByLabel('API key').fill(key)
  await page.getByRole('button', { name: 'Connect', exact: true }).click()
  await page.getByRole('button', { name: 'Find enrichment on SonoVault' }).click()
  await page.getByRole('button', { name: 'Apply candidate 7', exact: true }).click()
  await expect.poll(() => applied).toBe(true)
  await expect(page.getByRole('region', { name: 'Frozen enrichment review' })).toHaveCount(0)
})

    for (const { provider, artworkFails, changed, existing } of [
      { provider: 'sonovault', artworkFails: false, changed: false, existing: 'none' },
      { provider: 'beatport', artworkFails: false, changed: false, existing: 'none' },
      { provider: 'beatport', artworkFails: true, changed: false, existing: 'none' },
      { provider: 'sonovault', artworkFails: false, changed: true, existing: 'none' },
      { provider: 'beatport', artworkFails: false, changed: false, existing: 'preferred' },
      { provider: 'beatport', artworkFails: false, changed: false, existing: 'duplicate' },
    ] as const) {
      test(`direct ${provider} release enrichment handles tracks and artwork${artworkFails ? ' failure' : ''}${changed ? ' with changed provider data' : ''}${existing !== 'none' ? ` with ${existing} artwork` : ''}`, async ({ page }) => {
        const providerName = provider === 'beatport' ? 'Beatport' : 'SonoVault'
        const track = { id: '1', title: 'Track', isrc: 'GBABC2600001', discNumber: 1, trackNumber: 1, credits: [],
          recordingCandidate: { providerId: provider, candidateId: '1', editionId: null, revision: null, entityKind: 'Recording',
            match: { basis: 'MetadataSearch', explanation: 'Track evidence', conflicts: [] }, metadata: { name: 'Track', bpm: 124 },
            evidence: [{ field: 'Bpm', source: provider, sourceRecordId: '1', sourceUrl: null, retrievedAt: '2026-01-01', attribution: null }] } }
        const candidate = { providerId: provider, candidateId: '7', editionId: null, revision: null,
          match: { basis: 'MetadataSearch', explanation: 'Candidate evidence', conflicts: [] },
          metadata: { name: 'Release title', credits: [], releaseDetails: { reportedReleaseDate: null,
            selectedEditionId: null, editions: [], tracks: [track] } }, evidence: [] }
        const batchTrack = { ...track, recordingCandidate: { ...track.recordingCandidate,
          metadata: { ...track.recordingCandidate.metadata, bpm: changed ? 125 : 124 },
          evidence: [{ ...track.recordingCandidate.evidence[0], retrievedAt: '2026-01-02' }] } }
        const batchCandidate = { ...candidate, metadata: { ...candidate.metadata,
          releaseDetails: { ...candidate.metadata.releaseDetails, tracks: [batchTrack] } } }
        let tracksSaved = false
        let releaseSaved = false
        let artworkSaved = false
        await page.route(`**/api/v1/releases/release-fixture/enrichment/${provider}`, route => route.fulfill({ json: {
          providerId: provider, retrievedAt: '2026-11-07T00:00:00Z',
          query: { catalogId: 'release-fixture', catalogVersion: 'original-version', name: 'Release title', artistNames: [] },
          candidates: [candidate], warnings: [],
        } }))
        await page.route(`**/api/v1/releases/release-fixture/enrichment/${provider}/reviews`, route => route.fulfill({
          status: 201, headers: { ETag: '"release-review"' }, json: {
            id: 'release-review', version: 'release-review', entityId: 'release-fixture', entityKind: 'Release',
            candidate, fields: [{ field: 'Name', label: 'Title', current: 'Release title', proposed: 'Release title' }],
            warnings: [], outcome: null,
          },
        }))
        await page.route(`**/api/v1/releases/release-fixture/enrichment/${provider}/track-reviews`, route => route.fulfill({
          status: 201, headers: { ETag: '"track-review"' }, json: {
            id: 'track-review', version: 'track-review', releaseId: 'release-fixture', candidate: batchCandidate,
            warnings: ['Only the first 100 provider tracks are returned; the tracklist is incomplete.'],
            tracks: [
              { candidateId: '1', status: 'Confident', review: { id: 'child', fields: [
                { field: 'Bpm', current: null, proposed: 124 },
                { field: 'MusicalKey', current: null, proposed: { value: 'D Minor', notation: 'Musical' } },
                { field: 'PreferredGenre', current: null, proposed: ['House'] },
              ] } },
              { candidateId: '2', title: 'Uncertain track', status: 'Ambiguous', reasons: ['Different mix.'], review: null },
            ],
          },
        }))
        await page.route('**/api/v1/enrichment-track-reviews/track-review/apply', route => {
          expect(route.request().postDataJSON().selections).toEqual([{ reviewId: 'child', fields: ['Bpm', 'MusicalKey'], preferredGenre: null }])
          tracksSaved = true
          return route.fulfill({ json: { recordings: [{ entityId: 'recording-1' }] } })
        })
        await page.route('**/api/v1/enrichment-reviews/release-review/apply', route => {
          expect(tracksSaved).toBe(true)
          releaseSaved = true
          return route.fulfill({ json: { fields: ['Name'] } })
        })
        if (provider === 'beatport') {
          if (existing !== 'none') await page.route('**/api/v1/releases/release-fixture/artwork', route => route.fulfill({
            headers: { ETag: '"gallery"' }, json: { releaseId: 'release-fixture', items: [{
              id: 'existing', assetId: 'existing', role: 'cover', caption: null, isPreferred: true, mediaType: 'image/jpeg',
              byteLength: 10, width: 1, height: 1, originalFilename: null,
              sourceUrl: existing === 'duplicate' ? 'https://www.beatport.com/release/-/7' : 'https://example.test/cover',
              attribution: null, usageRights: 'Not recorded',
            }] },
          }))
          await page.route('**/api/v1/releases/release-fixture/artwork-candidates', route => route.fulfill({ json: {
            providers: ['beatport'], items: [{ provider: 'beatport', candidateId: 'release-fixture:7', revision: 'r1',
              title: 'Cover', sourceUrl: 'https://www.beatport.com/release/-/7', attribution: null }],
          } }))
          await page.route('**/api/v1/releases/release-fixture/artwork-candidates/attach', route => {
            expect(releaseSaved).toBe(true)
            expect(route.request().postDataJSON().isPreferred).toBe(existing === 'none')
            if (artworkFails) return route.fulfill({ status: 503, json: { detail: 'Artwork unavailable' } })
            artworkSaved = true
            return route.fulfill({ status: 201, headers: { ETag: '"new-gallery"' }, json: { items: [] } })
          })
        }
        await page.goto('/admin/#/releases/release-fixture/show?tab=Details')
        await page.getByLabel('API key').fill(key)
        await page.getByRole('button', { name: 'Connect', exact: true }).click()
        if (provider === 'beatport') {
          await page.getByRole('combobox', { name: 'Enrichment provider' }).click()
          await page.getByRole('option', { name: 'Beatport', exact: true }).click()
        }
        await page.getByRole('button', { name: `Find enrichment on ${providerName}` }).click()
        await page.getByRole('button', { name: 'Apply candidate 7', exact: true }).click()
        if (changed) {
          await expect(page.getByRole('alert').filter({ hasText: 'Provider release details changed while preparing tracks' })).toBeVisible()
          expect(tracksSaved).toBe(false)
          expect(releaseSaved).toBe(false)
        } else if (artworkFails) {
          await expect(page.getByRole('alert').filter({ hasText: 'Release metadata was saved; artwork may not have been attached.' })).toBeVisible()
        } else {
          await expect(page.getByText(/Updated 1 matched recordings; 1 track entries need attention/)).toBeVisible()
          await expect(page.getByText(/Only the first 100 provider tracks are returned/)).toBeVisible()
          await expect(page.getByText(provider === 'sonovault' ? /SonoVault does not supply artwork/
            : existing === 'duplicate' ? /Beatport artwork is already attached/ : /Beatport artwork attached/)).toBeVisible()
        }
        expect(artworkSaved).toBe(provider === 'beatport' && !artworkFails && existing !== 'duplicate')
      })
    }
for (const resource of ['recordings', 'releases'] as const) {
  for (const provider of ['sonovault', 'beatport'] as const) {
  test(`${resource} ${provider} reviewed enrichment is selective, explicit and refreshes saved metadata`, async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 844 })
    const recording = resource === 'recordings'
    const providerName = provider === 'beatport' ? 'Beatport' : 'SonoVault'
    const editionId = recording || provider === 'beatport' ? null : '12'
    const catalogLabel = provider === 'beatport' ? 'Beatport catalog number' : 'Selected edition catalog number'
    const id = recording ? 'release-recording-0' : 'release-fixture'
    let applied = false
    let lookups = 0
    let reviews = 0
    let applies = 0
    const candidate = {
      providerId: provider, candidateId: '7', editionId, revision: null,
      match: { basis: 'MetadataSearch', explanation: 'Candidate evidence', conflicts: ['Review canonical identity.'] },
      metadata: { name: 'Reviewed title', bpm: 124, genres: ['House'], subgenres: ['Progressive House'], credits: [] },
      evidence: [],
     }
    await page.route(`**/api/v1/${resource}/${id}`, async route => {
      const response = await route.fetch()
      const data = await response.json()
      if (applied) {
        data.title = 'Reviewed title'
        if (recording) data.musicalMetadata = { bpm: 124, keyValue: null, keyNotation: null, genres: ['House'],
          subgenres: ['Progressive House'], preferredGenre: 'Progressive House' }
        else data.catalogNumber = 'REVIEW1'
      }
      await route.fulfill({ response, json: data })
    })
    await page.route(`**/api/v1/${resource}/${id}/enrichment/${provider}`, async route => {
      lookups++
      await route.fulfill({ json: { providerId: provider, retrievedAt: '2026-11-07T00:00:00Z',
        query: { catalogId: id, catalogVersion: 'original-catalog-version', name: 'Original title',
          mixName: null, isrc: null, durationMs: null, artistNames: [] }, candidates: [candidate], warnings: [] } })
    })
    await page.route(`**/api/v1/${resource}/${id}/enrichment/${provider}/reviews`, async route => {
      reviews++
      expect(route.request().postDataJSON()).toEqual({
        candidateId: '7', editionId, catalogVersion: 'original-catalog-version',
      })
      await route.fulfill({ status: 201, headers: { ETag: '"frozen-review"' }, json: {
        id: `review-${reviews}`, version: 'frozen-version', entityId: id, entityKind: recording ? 'Recording' : 'Release',
        createdAt: '2026-11-07T00:00:00Z', candidate,
        fields: [{ field: 'Name', label: 'Title', current: 'Original title', proposed: 'Reviewed title' },
          ...(recording ? [
            { field: 'Bpm', label: 'BPM', current: null, proposed: 124 },
            { field: 'Genre', label: 'Genres (replace list)', current: [], proposed: ['House'] },
            { field: 'Subgenre', label: 'Subgenres (replace list)', current: [], proposed: ['Progressive House'] },
            { field: 'PreferredGenre', label: 'Preferred classification (choose one)', current: null, proposed: ['House', 'Progressive House'] },
          ] : [{ field: 'CatalogNumber', label: catalogLabel, current: null, proposed: 'REVIEW1' }]),
          { field: `${providerName}Identity`, label: `Accept ${providerName} identity`, current: [], proposed: '7' }],
        warnings: ['Credits, Label links and tracklists remain evidence.'], outcome: null,
      } })
    })
    await page.route('**/api/v1/enrichment-reviews/*/apply', async route => {
      applies++
      expect(route.request().headers()['if-match']).toBe('"frozen-review"')
      const body = route.request().postDataJSON()
      expect(body).toEqual({
        fields: recording ? ['Name', 'Bpm', 'Genre', 'Subgenre', 'PreferredGenre'] : ['Name', 'CatalogNumber'],
        preferredGenre: recording ? 'Progressive House' : null, reason: 'Reviewed the source match', confirmed: true,
      })
      if (applies === 1) return route.fulfill({ status: 412, json: {
        code: 'enrichment_state_changed', detail: 'The catalog changed. Prepare a fresh review; nothing was applied.',
      } })
      applied = true
      await route.fulfill({ json: { reviewId: `review-${reviews}`, entityId: id, appliedAt: '2026-11-07T01:00:00Z', fields: body.fields } })
    })
    await page.goto(`/admin/#/${resource}/${id}/show${recording ? '' : '?tab=Details'}`)
    await page.getByLabel('API key').fill(key)
    await page.getByRole('button', { name: 'Connect', exact: true }).click()
    if (provider === 'beatport') {
      await page.getByRole('combobox', { name: 'Enrichment provider' }).click()
      await page.getByRole('option', { name: 'Beatport', exact: true }).click()
    }
    const section = page.getByRole('region', { name: `${providerName} enrichment` })
    expect(lookups).toBe(0)
    await section.getByRole('button', { name: `Find enrichment on ${providerName}` }).click()
    expect(applies).toBe(0)
    const prepare = section.getByRole('button', { name: /Prepare review for candidate 7/ })
    for (let attempt = 0; attempt < 2; attempt++) {
      await prepare.click()
      const review = page.getByRole('region', { name: 'Frozen enrichment review' })
      const checkboxes = review.getByRole('checkbox')
      await expect.poll(() => checkboxes.evaluateAll(nodes => nodes.every(node => !(node as HTMLInputElement).checked))).toBe(true)
      const apply = review.getByRole('button', { name: 'Apply selected metadata' })
      await expect(apply).toBeDisabled()
      await review.getByRole('checkbox', { name: 'Title', exact: true }).check()
      if (recording) {
        for (const name of ['BPM', 'Genres (replace list)', 'Subgenres (replace list)', 'Preferred classification (choose one)'])
          await review.getByRole('checkbox', { name, exact: true }).check()
        await review.getByRole('combobox', { name: 'Preferred classification', exact: true }).click()
        await page.getByRole('option', { name: 'Progressive House', exact: true }).click()
      } else await review.getByRole('checkbox', { name: catalogLabel, exact: true }).check()
      await review.getByLabel('Enrichment review reason').fill('Reviewed the source match')
      await expect(apply).toBeDisabled()
      await review.getByRole('checkbox', { name: /I confirm these selected fields/ }).check()
      await expect(review.getByRole('checkbox', { name: `Accept ${providerName} identity`, exact: true })).not.toBeChecked()
      await apply.click()
      if (attempt === 0) {
        await expect(section.getByRole('alert').filter({ hasText: 'Prepare a fresh review' })).toBeVisible()
        await expect(page.getByRole('heading', { level: 1 })).not.toHaveText('Reviewed title')
      } else {
        await expect(review.getByRole('alert').filter({ hasText: 'was saved with provenance' })).toBeVisible()
        await expect(apply).toBeDisabled()
      }
    }
    await expect(page.getByRole('heading', { level: 1 })).toHaveText(recording ? 'Reviewed title Fixture Remix' : 'Reviewed title')
    if (recording) {
      await page.getByText('Details and provider identities', { exact: true }).click()
      await expect(page.getByText('Preferred classification: Progressive House', { exact: true })).toBeVisible()
    } else await expect(page.getByText(/Catalog number: REVIEW1/)).toBeVisible()
    expect(lookups).toBe(1); expect(reviews).toBe(2); expect(applies).toBe(2)
    const layout = await page.evaluate(() => ({
      width: document.documentElement.scrollWidth, viewport: window.innerWidth,
      outside: [...document.querySelectorAll('section,button,h1,h2,h3,div,label,p,span')].filter(element =>
        element.getBoundingClientRect().right > window.innerWidth).map(element => ({
          tag: element.tagName, text: element.textContent?.slice(0, 100), width: element.getBoundingClientRect().width,
        })).slice(-12),
    }))
    expect(layout.width, JSON.stringify(layout.outside)).toBeLessThanOrEqual(layout.viewport)
    await section.getByRole('combobox', { name: 'Enrichment provider' }).click()
    await page.getByRole('option', { name: provider === 'beatport' ? 'SonoVault' : 'Beatport', exact: true }).click()
    await expect(page.getByRole('region', { name: 'Frozen enrichment review' })).toHaveCount(0)
    expect(lookups).toBe(1); expect(applies).toBe(2)
  })
 }
}
