import { expect, test } from '@playwright/test'

test('Guided Release reconciliation verifies real synthetic copies, leaves a conflicting version and confirms once', async ({ page }, testInfo) => {
  await page.setViewportSize({ width: 390, height: 844 })
  await page.goto('/admin/#/media')
  await page.getByLabel('API key').fill('test-only-browser-key-not-a-production-secret')
  await page.getByRole('button', { name: 'Connect', exact: true }).click()
  await page.getByRole('button', { name: 'Organise Release Fixture Catalog Release', exact: true }).click()
  const guide = page.getByRole('region', { name: 'Guided Release reconciliation', exact: true })
  await expect(guide).toContainText('Choose Catalog Release')
  await guide.getByRole('button', { name: 'Find Release for track matching', exact: true }).click()
  await guide.getByRole('button', { name: 'Choose Release Fixture Catalog Release', exact: true }).click()
  await expect(guide).toContainText('2 supported proposals')
  await expect(guide).toContainText('1 need attention')
  await expect(guide).toContainText('Missing scanner SHA-256 hashes are calculated here')
  const verify = guide.getByRole('button', { name: 'Verify selected files and review', exact: true })
  await expect(verify).toBeDisabled()
  await guide.getByRole('button', { name: 'Select confident matches', exact: true }).click()
  await expect(guide).toContainText('2 selected copies across 1 Recordings')
  await guide.getByRole('button', { name: /Needs attention — 1 files/ }).click()
  await expect(guide.getByRole('menuitem')).toHaveCount(0)
  await expect(guide.getByRole('checkbox', { name: 'Include media 102', exact: true })).toBeDisabled()
  await page.screenshot({ path: testInfo.outputPath('track-proposals.png'), fullPage: true })
  const preparation = page.waitForRequest(request => request.url().endsWith('/media-reconciliation-reviews') && request.method() === 'POST')
  await verify.click()
  const prepare = await preparation
  expect(prepare.postDataJSON().selections).toEqual([
    { mediaId: 100, recordingId: 'release-recording-1' }, { mediaId: 101, recordingId: 'release-recording-1' },
  ])
  const review = guide.getByRole('region', { name: 'Verified Release reconciliation', exact: true })
  await expect(review).toContainText('2 selected files · 2 files verified')
  const confirm = review.getByRole('button', { name: 'Confirm Release and selected tracks', exact: true })
  await expect(confirm).toBeDisabled()
  await review.getByLabel('Reconciliation reason', { exact: true }).fill('Checked Release identity and original mix copies')
  await expect(confirm).toBeDisabled()
  await review.getByRole('checkbox', { name: "I confirm the entire group's Release and all selected Recording versions", exact: true }).check()
  await confirm.click()
  await expect(review.getByRole('alert')).toContainText('2 media copies linked and Release association saved')
  expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(390)
  await review.getByRole('button', { name: 'Continue with remaining files', exact: true }).click()
  await expect(guide).toContainText('2 already linked')
  await expect(verify).toBeDisabled()
  await expect(guide).toContainText('1 need attention')
})

for (const failure of ['verification', 'stale apply']) {
  test(`Guided reconciliation explicitly reports ${failure} and preserves the selections`, async ({ page }) => {
    const group = { key: 'mock-group', name: 'Mock Release', release: 'Mock Release', label: 'Fixture Label',
      fileCount: 1, libraries: ['JetDrive'], canAssociateRelease: true }
    const media = { id: 42, title: 'Mock Track', mix: 'Original Mix', artist: 'Fixture Artist', library: 'JetDrive' }
    const candidate = { recordingId: 'mock-recording', title: 'Mock Track', mix: 'Original Mix',
      status: 'Supported', evidence: ['ISRC, Artist, title, version and duration agree.'], blockers: [] }
    const plan = { groupKey: group.key, releaseId: 'mock-release', catalogTitle: 'Mock Release', version: 'plan-v1', evidence: ['Full group Release identity must be confirmed.'],
      tracks: [{ recordingId: candidate.recordingId, title: candidate.title, mix: candidate.mix, artists: ['Fixture Artist'] }],
      files: [{ media, candidates: [candidate], suggestedRecordingId: candidate.recordingId, linkedRecordingId: null, alreadyRegistered: false }],
      releaseAlreadyAssociated: false }
    await page.route('**/api/v1/media?*', route => route.fulfill({ json: {
      items: [], groups: new URL(route.request().url()).searchParams.has('groupKey') ? [] : [group],
      total: 1, groupTotal: 1, page: 1, pageSize: 25, libraries: ['JetDrive'], libraryNames: { JetDrive: 'Collection' }, warnings: [],
    } }))
    await page.route('**/api/v1/releases?*', route => route.fulfill({ json: {
      items: [{ id: 'mock-release', title: 'Mock Release' }], total: 1, page: 1, pageSize: 25,
    } }))
    await page.route('**/api/v1/media-reconciliation?*', route => route.fulfill({ headers: { ETag: '"proposal"' }, json: plan }))
    await page.route('**/api/v1/media-reconciliation-reviews', async route => {
      expect(route.request().headers()['if-match']).toBe('"proposal"')
      expect(route.request().postDataJSON().selections).toEqual([{ mediaId: 42, recordingId: 'mock-recording' }])
      await route.fulfill(failure === 'verification'
        ? { status: 409, json: { code: 'media_changed', detail: 'Selected file changed; no links were accepted.' } }
        : { status: 201, headers: { ETag: '"verified-batch"' }, json: {
          id: 'mock-review', version: 'v1', plan, selections: [{ mediaId: 42, recordingId: 'mock-recording' }], verifiedMediaIds: [42],
          createdAt: '2026-11-08T00:00:00Z', outcome: null,
        } })
    })
    await page.route('**/api/v1/media-reconciliation-reviews/mock-review/apply', async route => {
      expect(route.request().headers()['if-match']).toBe('"verified-batch"')
      await route.fulfill({ status: 412, json: { code: 'enrichment_state_changed', detail: 'Catalog tracks changed; prepare a fresh review.' } })
    })
    await page.goto('/admin/#/media')
    await page.getByLabel('API key').fill('test-only-browser-key-not-a-production-secret')
    await page.getByRole('button', { name: 'Connect', exact: true }).click()
    await page.getByRole('button', { name: 'Organise Release Mock Release', exact: true }).click()
    const guide = page.getByRole('region', { name: 'Guided Release reconciliation', exact: true })
    await guide.getByRole('button', { name: 'Find Release for track matching', exact: true }).click()
    await guide.getByRole('button', { name: 'Choose Release Mock Release', exact: true }).click()
    await guide.getByRole('button', { name: 'Select confident matches', exact: true }).click()
    await guide.getByRole('button', { name: 'Verify selected files and review', exact: true }).click()
    if (failure === 'verification') {
      await expect(guide.getByRole('alert').filter({ hasText: 'Selected file changed' })).toBeVisible()
      await expect(guide).toContainText('1 selected copies across 1 Recordings')
      await expect(guide.getByRole('button', { name: 'Confirm Release and selected tracks', exact: true })).toHaveCount(0)
    } else {
      await guide.getByLabel('Reconciliation reason', { exact: true }).fill('Reviewed versions')
      await guide.getByRole('checkbox', { name: "I confirm the entire group's Release and all selected Recording versions", exact: true }).check()
      await guide.getByRole('button', { name: 'Confirm Release and selected tracks', exact: true }).click()
      await expect(guide.getByRole('alert').filter({ hasText: 'Catalog tracks changed' })).toBeVisible()
      await expect(guide.getByRole('alert').filter({ hasText: 'association saved' })).toHaveCount(0)
      await guide.getByRole('button', { name: 'Back to track selection', exact: true }).click()
      await expect(guide).toContainText('1 selected copies across 1 Recordings')
    }
  })
}
