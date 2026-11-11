import { expect, test } from '@playwright/test'

for (const stale of [false, true]) {
  test(`Media identity review ${stale ? 'reports changed evidence' : 'saves only with explicit confirmation'}`, async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 844 })
    let prepares = 0
    let applies = 0
    const media = { id: 42, library: 'JetDrive', relativePath: 'AIFF/Test Label/Test release/tune.aiff',
      path: '/test-library/AIFF/Test Label/Test release/tune.aiff', title: 'Catalog Track 1 (Fixture Remix)',
      mix: null, artist: 'Fixture Composer', album: 'Fixture release', isrc: 'RELTEST0', durationSeconds: 123.456,
      contentHash: 'a'.repeat(64), sizeBytes: 4, scannedUtc: '2026-11-07T00:00:00Z' }
    const candidate = { recordingId: 'release-recording-0', title: 'Catalog Track 1', mix: 'Fixture Remix',
      status: 'Supported', evidence: ['Tagged title, mix, Artist and duration agree.'], blockers: [] }
    await page.route('**/api/v1/media?*', route => route.fulfill({ json: {
      items: [media], total: 1, page: 1, pageSize: 25, libraries: ['JetDrive', 'Dropbox', 'NAS'],
      warnings: ['Historical inventory, not backup verification.'],
    } }))
    await page.route('**/api/v1/media/42/candidates', route => route.fulfill({ json: {
      media, linkedRecordingId: null, candidates: [candidate,
        { ...candidate, recordingId: 'blocked', status: 'Blocked', blockers: ['Duration conflicts.'] }],
    } }))
    await page.route('**/api/v1/media/42/link-reviews', async route => {
      prepares++
      expect(route.request().postDataJSON()).toEqual({ recordingId: 'release-recording-0' })
      await route.fulfill({ status: 201, headers: { ETag: '"media-original"' }, json: {
        id: 'media-review', version: 'frozen-version', media, candidate, createdAt: '2026-11-07T00:00:00Z',
        verifiedSha256: 'a'.repeat(64), sidecarTitle: 'Catalog Track 1', sidecarMix: 'Fixture Remix', outcome: null,
      } })
    })
    await page.route('**/api/v1/media-link-reviews/media-review/apply', async route => {
      applies++
      expect(route.request().headers()['if-match']).toBe('"media-original"')
      expect(route.request().postDataJSON()).toEqual({ reason: 'Verified same audio and version', confirmed: true })
      await route.fulfill(stale ? { status: 412, json: { code: 'enrichment_state_changed', detail: 'Scanner evidence changed; nothing linked.' } } :
        { json: { reviewId: 'media-review', mediaId: 'scanner-copy', recordingId: 'release-recording-0', appliedAt: '2026-11-07T00:00:00Z' } })
    })
    await page.goto('/admin/#/media')
    await page.getByLabel('API key').fill('test-only-browser-key-not-a-production-secret')
    await page.getByRole('button', { name: 'Connect', exact: true }).click()
    await expect(page.getByRole('table', { name: 'Media inventory' })).toBeVisible()
    expect(prepares).toBe(0)
    await page.getByRole('button', { name: 'Review media 42' }).click()
    await expect(page.getByRole('button', { name: 'Verify file and prepare link to blocked', exact: true })).toBeDisabled()
    await page.getByRole('button', { name: 'Verify file and prepare link to release-recording-0', exact: true }).click()
    const review = page.getByRole('region', { name: 'Frozen media link review' })
    const accept = review.getByRole('button', { name: 'Accept media Recording link' })
    await expect(accept).toBeDisabled()
    await review.getByLabel('Media link review reason').fill('Verified same audio and version')
    await expect(accept).toBeDisabled()
    await review.getByRole('checkbox', { name: /I confirm this file/ }).check()
    await accept.click()
    await expect(page.getByRole('alert').filter({ hasText: stale ? 'nothing linked' : 'permanent audit' })).toBeVisible()
    expect(prepares).toBe(1); expect(applies).toBe(1)
    expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(390)
    if (!stale) {
      await review.getByRole('link', { name: 'View Recording and linked copies' }).click()
      await page.getByRole('button', { name: /Media locations and file details/ }).click()
      await expect(page.getByRole('region', { name: 'Linked Recording media' })).toBeVisible()
    }
  })
}
