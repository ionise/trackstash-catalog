import { expect, test } from '@playwright/test'

const key = 'test-only-browser-key-not-a-production-secret'

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
    if (recording) await expect(page.getByText('Preferred classification: Progressive House', { exact: true })).toBeVisible()
    else await expect(page.getByText(/Catalog number: REVIEW1/)).toBeVisible()
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
