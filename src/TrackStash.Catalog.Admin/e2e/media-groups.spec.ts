import { expect, test } from '@playwright/test'

for (const stale of [false, true]) {
  test(`Grouped media browsing and Release association ${stale ? 'reports stale group' : 'saves explicit intent'}`, async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 844 })
    let prepares = 0
    let applies = 0
    const group = { key: 'group-key', name: 'Fixture release', label: 'Test Label', release: 'Fixture release',
      fileCount: 40, libraries: ['JetDrive', 'Dropbox', 'NAS'], canAssociateRelease: true }
    const media = { id: 42, library: 'JetDrive', path: '/secret-detail-only/tune.aiff', relativePath: 'secret-detail-only/tune.aiff',
      title: 'Fixture tune', artist: 'Fixture Artist', label: 'Test Label', release: 'Fixture release', scannedUtc: '2026-11-08', sizeBytes: 4 }
    await page.route('**/api/v1/media?*', async route => {
      const query = new URL(route.request().url()).searchParams
      expect(query.get('groupBy')).toBe('release')
      if (query.has('groupKey')) expect(query.get('groupKey')).toBe('group-key')
      await route.fulfill({ json: { items: query.has('groupKey') ? [media] : [], groups: query.has('groupKey') ? [] : [group],
        total: 40, groupTotal: 1, page: Number(query.get('page')), pageSize: 25, libraries: ['JetDrive', 'Dropbox', 'NAS'],
        libraryNames: { JetDrive: 'Travel collection', Dropbox: 'DJ selections', NAS: 'Archive' }, warnings: [] } })
    })
    await page.route('**/api/v1/releases?*', route => route.fulfill({ json: {
      items: [{ id: 'release-fixture', title: 'Fixture Catalog Release', labelName: 'Test Label' }], total: 1, page: 1, pageSize: 25,
    } }))
    await page.route('**/api/v1/media-release-reviews', async route => {
      prepares++
      expect(route.request().postDataJSON()).toEqual({ groupKey: 'group-key', releaseId: 'release-fixture' })
      await route.fulfill({ status: 201, headers: { ETag: '"original-group-review"' }, json: {
        id: 'group-review', version: 'group-version', groupKey: 'group-key', label: 'Test Label',
        releaseTitle: 'Fixture release', releaseId: 'release-fixture', catalogTitle: 'Fixture Catalog Release',
        files: [media], evidence: ['Entire group frozen. Title differs; explicit review required.'], createdAt: '2026-11-08T00:00:00Z', outcome: null,
      } })
    })
    await page.route('**/api/v1/media-release-reviews/group-review/apply', async route => {
      applies++
      expect(route.request().headers()['if-match']).toBe('"original-group-review"')
      expect(route.request().postDataJSON()).toEqual({ reason: 'Reviewed Label and Release metadata', confirmed: true })
      await route.fulfill(stale ? { status: 412, json: { code: 'enrichment_state_changed', detail: 'Media group changed; prepare a fresh review.' } } :
        { json: { reviewId: 'group-review', groupKey: 'group-key', releaseId: 'release-fixture', releaseTitle: 'Fixture Catalog Release',
          mediaReleaseTitle: 'Fixture release', label: 'Test Label', fileCount: 1, libraries: ['JetDrive'], appliedAt: '2026-11-08T00:00:00Z' } })
    })
    await page.goto('/admin/#/media')
    await page.getByLabel('API key').fill('test-only-browser-key-not-a-production-secret')
    await page.getByRole('button', { name: 'Connect', exact: true }).click()
    const groups = page.getByRole('table', { name: 'Media groups' })
    await expect(groups).toBeVisible()
    await expect(groups).toContainText('Travel collection, DJ selections, Archive')
    await expect(page.getByRole('button', { name: 'Next', exact: true })).toBeDisabled()
    await page.getByRole('button', { name: 'Organise Release Fixture release' }).click()
    await page.getByRole('checkbox', { name: 'Show individual file reviews (advanced)' }).check()
    await expect(page.getByRole('table', { name: 'Media inventory' })).toContainText('Travel collection')
    await expect(page.getByRole('table', { name: 'Media inventory' })).not.toContainText('secret-detail-only')
    await expect(page.getByRole('button', { name: 'Next', exact: true })).toBeEnabled()
    expect(prepares).toBe(0)
    await page.getByRole('button', { name: 'Release association only (without track links)' }).click()
    await page.getByRole('button', { name: 'Find Catalog Releases' }).click()
    await page.getByRole('button', { name: 'Review association to release-fixture' }).click()
    const review = page.getByRole('region', { name: 'Frozen media Release review' })
    const accept = review.getByRole('button', { name: 'Accept Release association' })
    await expect(accept).toBeDisabled()
    await review.getByLabel('Release association reason').fill('Reviewed Label and Release metadata')
    await expect(accept).toBeDisabled()
    await review.getByRole('checkbox', { name: /I confirm the entire media group/ }).check()
    await accept.click()
    await expect(page.getByRole('alert').filter({ hasText: stale ? 'Media group changed' : 'association saved' })).toBeVisible()
    expect(prepares).toBe(1); expect(applies).toBe(1)
    expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(390)
    await page.getByRole('button', { name: 'Back to groups' }).click()
    await page.getByLabel('Filter label', { exact: true }).fill('Test Label')
    await page.getByLabel('Filter artist', { exact: true }).fill('Fixture Artist')
    const filtered = page.waitForRequest(request => {
      if (!request.url().includes('/api/v1/media?')) return false
      const query = new URL(request.url()).searchParams
      return query.get('label') === 'Test Label' && query.get('artist') === 'Fixture Artist' && !query.has('groupKey')
    })
    await page.getByRole('button', { name: 'Search', exact: true }).click()
    await filtered
    await expect(groups).toBeVisible()
  })
}
