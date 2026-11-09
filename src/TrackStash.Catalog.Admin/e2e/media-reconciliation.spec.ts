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
  await page.goto('/admin/#/recordings/release-recording-1/show')
  await page.getByRole('button', { name: 'Media locations and file details — 2 linked copies', exact: true }).click()
  const copies = page.getByRole('region', { name: 'Linked Recording media', exact: true })
  await expect(copies).toContainText('2 copies share an exact stored audio fingerprint')
  await expect(copies.getByText('Audio group 1 · matches 1 other copy', { exact: true })).toHaveCount(2)
  await expect(copies.getByText('SHA-256 (whole-file bytes):', { exact: false }).first()).not.toBeVisible()
  await copies.getByRole('button', { name: /Location and file details/ }).first().click()
  await expect(copies.getByText('SHA-256 (whole-file bytes):', { exact: false }).first()).toBeVisible()
  expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(390)
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

test('Recording copies distinguish unavailable and different fingerprints without claiming different audio', async ({ page }) => {
      await page.route('**/api/v1/recordings/release-recording-1/media', route => route.fulfill({ json: [
        { id: 'copy-a', path: '/private/full-path/a.aiff', libraryName: 'Main collection', format: 'AIFF', fingerprintKey: 'audio-a', sha256: 'hash-a' },
        { id: 'copy-b', path: '/private/full-path/b.flac', libraryName: 'Backup collection', format: 'FLAC', fingerprintKey: 'audio-b', sha256: 'hash-b' },
        { id: 'copy-c', path: '/private/full-path/c.aiff', libraryName: 'DJ collection', format: 'AIFF', fingerprintKey: null, sha256: 'hash-c' },
      ] }))
      await page.goto('/admin/#/recordings/release-recording-1/show')
      await page.getByLabel('API key').fill('test-only-browser-key-not-a-production-secret')
      await page.getByRole('button', { name: 'Connect', exact: true }).click()
      const copies = page.getByRole('region', { name: 'Linked Recording media', exact: true })
      await page.getByRole('button', { name: /Media locations and file details/ }).click()
      await expect(copies).toContainText('2 different stored fingerprint groups')
      await expect(copies).toContainText('Different fingerprints do not necessarily mean different audio')
      await expect(copies).toContainText('1 copies have no stored fingerprint')
      await expect(copies.getByText('Main collection', { exact: true })).toBeVisible()
      await expect(copies.getByText('/private/full-path/a.aiff', { exact: true })).not.toBeVisible()
      await copies.getByRole('button', { name: 'Location and file details — Main collection AIFF' }).click()
      await expect(copies.getByText('/private/full-path/a.aiff', { exact: true })).toBeVisible()
})
