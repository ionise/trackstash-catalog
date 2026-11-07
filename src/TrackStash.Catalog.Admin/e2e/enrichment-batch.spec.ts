import { expect, test } from '@playwright/test'

const key = 'test-only-browser-key-not-a-production-secret'

for (const stale of [false, true]) {
  test(`Release matched track review is selective, bounded and ${stale ? 'reports stale state' : 'applies atomically'}`, async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 844 })
    let lookups = 0
    let prepares = 0
    let applies = 0
    const candidate = { providerId: 'beatport', candidateId: '10', editionId: null,
      match: { basis: 'MetadataSearch', explanation: 'Release candidate', conflicts: [] }, metadata: { name: 'Release', credits: [] }, evidence: [] }
    await page.route('**/api/v1/releases/release-fixture/enrichment/beatport', async route => {
      lookups++
      await route.fulfill({ json: { providerId: 'beatport', retrievedAt: '2026-11-07T00:00:00Z',
        query: { catalogVersion: 'original-release', name: 'Release', artistNames: [] }, candidates: [candidate], warnings: [] } })
    })
    await page.route('**/api/v1/releases/release-fixture/enrichment/beatport/track-reviews', async route => {
      prepares++
      expect(route.request().postDataJSON()).toEqual({ candidateId: '10', catalogVersion: 'original-release' })
      await route.fulfill({ status: 201, headers: { ETag: '"frozen-batch"' }, json: {
        id: 'batch-1', version: 'frozen-version', releaseId: 'release-fixture', createdAt: '2026-11-07T00:00:00Z',
        candidate, warnings: ['All selected Recordings apply together or none do.'], outcome: null,
        tracks: [
          { candidateId: '7', title: 'Matched tune', status: 'Confident', recordingIds: ['release-recording-0'],
            reasons: ['Accepted track ID, exact title and mix agreement.'], review: {
              id: 'child-7', entityId: 'release-recording-0', candidate: { ...candidate, candidateId: '7' },
              fields: [
                { field: 'Name', label: 'Title', current: 'Matched tune', proposed: 'Matched tune' },
                { field: 'Bpm', label: 'BPM', current: null, proposed: 124 },
                { field: 'Genre', label: 'Genres (replace list)', current: [], proposed: ['House'] },
                { field: 'Subgenre', label: 'Subgenres (replace list)', current: null, proposed: ['Progressive House'] },
                { field: 'PreferredGenre', label: 'Preferred classification (choose one)', current: null, proposed: ['House', 'Progressive House'] },
                { field: 'BeatportIdentity', label: 'Accept Beatport identity', current: [], proposed: '7' },
              ] } },
          { candidateId: '8', title: 'Ambiguous version', status: 'Ambiguous', recordingIds: ['other-recording'],
            reasons: ['Mix conflicts.'], review: null },
          { candidateId: '9', title: 'Unmatched tune', status: 'Unmatched', recordingIds: [], reasons: ['No exact match.'], review: null },
        ],
      } })
    })
    await page.route('**/api/v1/enrichment-track-reviews/batch-1/apply', async route => {
      applies++
      expect(route.request().headers()['if-match']).toBe('"frozen-batch"')
      expect(route.request().postDataJSON()).toEqual({
        selections: [{ reviewId: 'child-7', fields: ['Bpm', 'Genre', 'Subgenre', 'PreferredGenre'], preferredGenre: 'Progressive House' }],
        reason: 'Checked the Release and Recording versions', confirmed: true,
      })
      await route.fulfill(stale ? { status: 412, json: { code: 'enrichment_state_changed',
        detail: 'The catalog changed. Prepare a fresh review; nothing was applied.' } } : { json: {
        reviewId: 'batch-1', recordings: [{ entityId: 'release-recording-0', fields: ['Bpm', 'Genre', 'Subgenre', 'PreferredGenre'] }],
      } })
    })
    await page.goto('/admin/#/releases/release-fixture/show?tab=Details')
    await page.getByLabel('API key').fill(key)
    await page.getByRole('button', { name: 'Connect', exact: true }).click()
    await page.getByRole('combobox', { name: 'Enrichment provider' }).click()
    await page.getByRole('option', { name: 'Beatport', exact: true }).click()
    expect(lookups).toBe(0)
    await page.getByRole('button', { name: 'Find enrichment on Beatport' }).click()
    await page.getByRole('button', { name: 'Review matched tracks for candidate 10' }).click()
    const batch = page.getByRole('region', { name: 'Frozen matched track review' })
    await expect(batch).toBeVisible()
    const matched = batch.getByRole('region', { name: 'Track match 7', exact: true })
    for (const label of ['BPM', 'Genres (replace list)', 'Subgenres (replace list)'])
      await expect(matched.getByRole('checkbox', { name: label, exact: true })).toBeChecked()
    await expect(matched.getByRole('checkbox', { name: 'Title', exact: true })).not.toBeChecked()
    await expect(matched.getByRole('checkbox', { name: 'Accept Beatport identity' })).not.toBeChecked()
    await expect(batch.getByRole('region', { name: 'Track match 8', exact: true }).getByRole('checkbox')).toHaveCount(0)
    await expect(batch.getByRole('region', { name: 'Track match 9', exact: true }).getByRole('checkbox')).toHaveCount(0)
    const apply = batch.getByRole('button', { name: 'Apply selected track metadata' })
    await expect(apply).toBeDisabled()
    await matched.getByRole('checkbox', { name: 'Preferred classification (choose one)', exact: true }).check()
    await matched.getByRole('combobox', { name: 'Preferred classification for 7' }).click()
    await page.getByRole('option', { name: 'Progressive House', exact: true }).click()
    await batch.getByLabel('Track batch review reason').fill('Checked the Release and Recording versions')
    await expect(apply).toBeDisabled()
    await batch.getByRole('checkbox', { name: /I confirm this provider Release/ }).check()
    await apply.click()
    if (stale) {
      await expect(page.getByRole('alert').filter({ hasText: 'nothing was applied' })).toBeVisible()
      await expect(matched.getByRole('checkbox', { name: 'BPM', exact: true })).toBeEnabled()
    } else {
      await expect(batch.getByRole('alert').filter({ hasText: 'saved atomically' })).toBeVisible()
      await expect(apply).toBeDisabled()
      await expect(matched.getByRole('checkbox', { name: 'BPM', exact: true })).toBeDisabled()
    }
    expect(lookups).toBe(1); expect(prepares).toBe(1); expect(applies).toBe(1)
    expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(390)
    await page.getByRole('combobox', { name: 'Enrichment provider' }).click()
    await page.getByRole('option', { name: 'SonoVault', exact: true }).click()
    await expect(batch).toHaveCount(0)
    expect(lookups).toBe(1)
  })
}
