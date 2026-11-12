import { expect, test } from '@playwright/test'

async function login(page: import('@playwright/test').Page) {
  await page.goto('/admin/#/media-matches')
  await page.getByLabel('API key').fill('test-only-browser-key-not-a-production-secret')
  await page.getByRole('button', { name: 'Connect', exact: true }).click()
}

test('A mismatched file can be manually assigned and later reassigned with explicit review', async ({ page }) => {
  await login(page)
  await page.getByLabel('Filter track', { exact: true }).fill('Manual worklist unrelated')
  await page.getByRole('button', { name: 'Search', exact: true }).click()
  const worklist = page.getByRole('region', { name: 'Bulk media matching', exact: true })
  await worklist.getByRole('button', { name: 'Manually assign media 203', exact: true }).click()
  const dialog = page.getByRole('dialog', { name: 'Manually assign media to a Recording' })
  await dialog.getByLabel('Search Catalog Recordings').fill('Catalog Track 3')
  await dialog.getByRole('button', { name: 'Search Recordings', exact: true }).click()
  await dialog.getByRole('button', { name: 'Choose Catalog Track 3 Original Mix', exact: true }).click()
  await dialog.getByRole('button', { name: 'Verify file and review assignment', exact: true }).click()
  await expect(dialog).toContainText('Tagged title conflicts.')
  await expect(dialog.getByRole('button', { name: 'Confirm manual assignment', exact: true })).toBeDisabled()
  await dialog.getByRole('checkbox', { name: 'I reviewed the Recording and any conflicts and confirm this assignment' }).check()
  await dialog.getByRole('button', { name: 'Confirm manual assignment', exact: true }).click()
  await expect(dialog).toContainText('Assignment saved to Recording release-recording-2.')
  await dialog.getByRole('button', { name: 'Close', exact: true }).click()
  await worklist.getByRole('button', { name: 'Reassign media 203', exact: true }).click()
  await dialog.getByLabel('Search Catalog Recordings').fill('Catalog Track 2')
  await dialog.getByRole('button', { name: 'Search Recordings', exact: true }).click()
  await dialog.getByRole('button', { name: 'Choose Catalog Track 2 Original Mix', exact: true }).click()
  await dialog.getByRole('button', { name: 'Verify file and review assignment', exact: true }).click()
  await expect(dialog).toContainText('This replaces the existing link to Recording release-recording-2.')
  await dialog.getByRole('checkbox', { name: 'I reviewed the Recording and any conflicts and confirm this assignment' }).check()
  await dialog.getByRole('button', { name: 'Confirm manual assignment', exact: true }).click()
  await expect(dialog).toContainText('Assignment saved to Recording release-recording-1.')
  await dialog.getByRole('button', { name: 'Close', exact: true }).click()
  await expect(worklist.getByRole('link', { name: 'Already linked', exact: true })).toHaveAttribute('href', /release-recording-1/)
})

test('A manual assignment with an uncertain response recovers the saved outcome after reload', async ({ page }) => {
  const media = { id: 42, title: 'Unmatched', mix: null, artist: 'Artist', library: 'JetDrive', path: '/mock/track.flac' }
  const candidate = { recordingId: 'target', title: 'Target', mix: 'Original Mix', status: 'Blocked',
    evidence: [], blockers: ['Tagged title conflicts.'] }
  const plan = { version: 'v1', files: [{ media, candidates: [], suggestedRecordingId: null,
    linkedRecordingId: null, alreadyRegistered: false, classification: 'unclassified' }] }
  const review = { id: 'manual-batch', version: 'v1', media, candidate, previousRecordingId: null,
    verifiedSha256: 'hash', createdAt: '2026-11-09T00:00:00Z', outcome: null }
  await page.route('**/api/v1/media?*', route => route.fulfill({ json: {
    items: [media], total: 1, page: 1, pageSize: 100, libraries: ['JetDrive'], warnings: [],
  } }))
  await page.route('**/api/v1/media-match-proposals', route => route.fulfill({ headers: { ETag: '"proposal"' }, json: plan }))
  await page.route('**/api/v1/tracks?*', route => route.fulfill({ json: { page: 1, pageSize: 25, total: 1,
    items: [{ recordingId: 'target', title: 'Target', mixName: 'Original Mix', recordingCredits: [],
      durationMs: null, isrc: null, releaseTitle: null }] } }))
  await page.route('**/api/v1/manual-media-reviews', route => route.fulfill({ status: 201, headers: { ETag: '"manual"' }, json: review }))
  await page.route('**/api/v1/manual-media-reviews/manual-batch/apply', route =>
    route.fulfill({ status: 503, json: { detail: 'Connection interrupted; check saved outcome.' } }))
  await page.route('**/api/v1/manual-media-reviews/manual-batch', route => route.fulfill({ headers: { ETag: '"manual"' }, json: {
    ...review, outcome: { reviewId: review.id, mediaId: 'scanner-42', recordingId: 'target', appliedAt: '2026-11-09T00:00:00Z' },
  } }))
  await login(page)
  await page.getByRole('button', { name: 'Manually assign media 42', exact: true }).click()
  const dialog = page.getByRole('dialog', { name: 'Manually assign media to a Recording' })
  await dialog.getByRole('button', { name: 'Search Recordings', exact: true }).click()
  await dialog.getByRole('button', { name: 'Choose Target Original Mix', exact: true }).click()
  await dialog.getByRole('button', { name: 'Verify file and review assignment', exact: true }).click()
  await dialog.getByRole('checkbox', { name: 'I reviewed the Recording and any conflicts and confirm this assignment' }).check()
  await dialog.getByRole('button', { name: 'Confirm manual assignment', exact: true }).click()
  await expect(dialog).toContainText('Connection interrupted; check saved outcome.')
  await page.reload()
  await page.getByLabel('API key').fill('test-only-browser-key-not-a-production-secret')
  await page.getByRole('button', { name: 'Connect', exact: true }).click()
  await expect(dialog).toContainText('Assignment saved to Recording target.')
  expect(await page.evaluate(() => sessionStorage.getItem('trackstash.manualMedia.pending'))).toBeNull()
})

test('Blocked Recording choices explain their reasons without opening evidence', async ({ page }) => {
  const media = { id: 42, title: 'Track', mix: null, artist: 'Artist', library: 'Dropbox', path: '/mock/track.flac' }
  const unmatched = { ...media, id: 43, title: 'Unmatched' }
  const blocked = { recordingId: 'blocked-recording', title: 'Track', mix: 'Original Mix', status: 'Blocked',
    evidence: [], blockers: ['Tagged ISRC conflicts.', 'Duration differs by 5000 ms; investigate version/audio before linking.'] }
  const available = { recordingId: 'available-recording', title: 'Track', mix: 'Radio Edit', status: 'Needs manual version review',
    evidence: [], blockers: [] }
  const plan = { version: 'v1', files: [
    { media, candidates: [blocked, available], suggestedRecordingId: null, linkedRecordingId: null, alreadyRegistered: false, classification: 'unclassified' },
    { media: unmatched, candidates: [], suggestedRecordingId: null, linkedRecordingId: null, alreadyRegistered: false, classification: 'unclassified' },
  ] }
  await page.route('**/api/v1/media?*', route => route.fulfill({ json: {
    items: [media, unmatched], total: 2, page: 1, pageSize: 100, libraries: ['Dropbox'], warnings: [],
  } }))
  await page.route('**/api/v1/media-match-proposals', route => route.fulfill({ headers: { ETag: '"proposal"' }, json: plan }))
  await login(page)
  const worklist = page.getByRole('region', { name: 'Bulk media matching', exact: true })
  const warning = worklist.getByRole('alert').filter({ hasText: 'Cannot select Track Original Mix (blocked-recording)' })
  await expect(warning).toBeVisible()
  await expect(warning).toContainText('Tagged ISRC conflicts.')
  await expect(warning).toContainText('Duration differs by 5000 ms')
  await expect(worklist).toContainText('No existing Catalog match found using tagged ISRC, title or exact-fingerprint copies.')
  await expect(worklist.getByRole('checkbox', { name: 'Select media 43', exact: true })).toBeDisabled()
  await worklist.getByLabel('Recording for media 42', { exact: true }).click()
  await expect(page.getByRole('option', { name: /Track Original Mix/ })).toHaveAttribute('aria-disabled', 'true')
  await page.getByRole('option', { name: /Track Radio Edit/ }).click()
  await expect(worklist.getByRole('checkbox', { name: 'Select media 42', exact: true })).toBeChecked()
  await expect(warning).toBeVisible()
})

test('Linked audio with a conflicting tagged ISRC remains an explicit selectable Recording choice', async ({ page }) => {
  const media = { id: 42, title: 'Absurd (Whitewash Edit)', mix: null, artist: 'Fluke', library: 'JetDrive', path: '/mock/track.flac' }
  const candidate = { recordingId: 'linked-recording', title: 'Absurd', mix: 'Whitewash Edit',
    status: 'Needs manual review: matches linked audio',
    evidence: ['Exact stored audio fingerprint matches an already linked copy.', 'Metadata warning: Tagged ISRC conflicts.'], blockers: [] }
  const plan = { version: 'v1', files: [{ media, candidates: [candidate], suggestedRecordingId: null,
    linkedRecordingId: null, alreadyRegistered: false, classification: 'unclassified' }] }
  await page.route('**/api/v1/media?*', route => route.fulfill({ json: {
    items: [media], total: 1, page: 1, pageSize: 100, libraries: ['JetDrive'], warnings: [],
  } }))
  await page.route('**/api/v1/media-match-proposals', route => route.fulfill({ headers: { ETag: '"proposal"' }, json: plan }))
  await page.route('**/api/v1/media-match-reviews', async route => {
    expect(route.request().postDataJSON().selections).toEqual([{ mediaId: 42, recordingId: 'linked-recording', classification: null }])
    await route.fulfill({ status: 201, headers: { ETag: '"frozen"' }, json: {
      id: 'linked-copy-batch', version: 'v1', plan, selections: route.request().postDataJSON().selections,
      verifiedMediaIds: [42, 43], createdAt: '2026-11-09T00:00:00Z', outcome: null,
    } })
  })
  await page.route('**/api/v1/media-match-reviews/linked-copy-batch/apply', route => route.fulfill({ json: {
    reviewId: 'linked-copy-batch', links: [{ recordingId: candidate.recordingId }], classifications: [], appliedAt: '2026-11-09T00:00:00Z',
  } }))
  await login(page)
  const worklist = page.getByRole('region', { name: 'Bulk media matching', exact: true })
  await expect(worklist).toContainText('Tags differ or are incomplete')
  await worklist.getByRole('button', { name: 'Select supported proposals on this page', exact: true }).click()
  await expect(worklist).toContainText('0 selected files')
  await worklist.getByLabel('Recording for media 42', { exact: true }).click()
  await page.getByRole('option', { name: /Absurd Whitewash Edit/ }).click()
  await expect(worklist.getByRole('checkbox', { name: 'Select media 42', exact: true })).toBeChecked()
  await expect(worklist.getByRole('alert').filter({ hasText: 'Metadata warning: Tagged ISRC conflicts.' })).toBeVisible()
  await worklist.getByRole('button', { name: 'Accept selected (1)', exact: true }).click()
  await expect(worklist.getByRole('alert').filter({ hasText: '1 copies linked; 0 classifications saved.' })).toBeVisible()
})

test('An unmatched file can find a Beatport Release, admit it and link in the same guided dialog', async ({ page }) => {
  const source = { id: 930, name: 'Media Admission Release', artists: [{ id: 987123, name: 'Media Admission Artist' }],
    remixers: [], label: { id: 42, name: 'Test label' }, catalogLabelIds: ['legacy-test'], catalogReleaseIds: [],
    inPurchaseHistory: false, purchaseSourceComplete: null }
  await page.route('**/api/v1/beatport/releases?*', route => {
    expect(new URL(route.request().url()).searchParams.get('q')).toBe('Media Admission Release')
    return route.fulfill({ json: { items: [source], total: 1, page: 1, pageSize: 10 } })
  })
  await page.route('**/api/v1/beatport/releases/930', route => route.fulfill({ json: { release: source, trackTotal: 1, tracks: [
    { id: 9301, name: 'Media admission tune', mixName: 'Original Mix', artists: source.artists, isrc: 'GBABC2600001' },
  ] } }))
  // Metadata is already cached in the disposable fixture; only the network fetch is simulated.
  await page.route('**/api/v1/beatport/releases/930/prepare-import', route => route.fulfill({ json: { releaseId: 930, sourceCompleteAfter: true } }))
  await login(page)
  await page.getByLabel('Filter track', { exact: true }).fill('Media admission tune')
  await page.getByRole('button', { name: 'Search', exact: true }).click()
  await page.getByRole('button', { name: 'Find Release and admit for media 300', exact: true }).click()
  const dialog = page.getByRole('dialog', { name: 'Find Release, admit and link media', exact: true })
  await expect(dialog.getByLabel('Beatport Release name', { exact: true })).toHaveValue('Media Admission Release')
  await dialog.getByRole('button', { name: 'Find Beatport Release', exact: true }).click()
  await dialog.getByRole('button', { name: 'Review Release Media Admission Release', exact: true }).click()
  const importer = dialog.getByRole('region', { name: 'Immutable catalog import review', exact: true })
  await importer.getByRole('button', { name: 'Create immutable import preview', exact: true }).click()
  await importer.getByRole('button', { name: 'Bulk accept straightforward identities', exact: true }).click()
  await importer.getByRole('checkbox', { name: 'I reviewed the complete release and identity choices, and confirm this catalog operation', exact: true }).check()
  await importer.getByRole('button', { name: 'Apply reviewed catalog import', exact: true }).click()
  await expect(dialog).toContainText('Release is in the Catalog.')
  const matches = dialog.getByRole('region', { name: 'Bulk media matching', exact: true })
  await matches.getByRole('button', { name: 'Select supported proposals on this page', exact: true }).click()
  await expect(matches).toContainText('1 selected files')
  let interrupted = false
  await page.route('**/api/v1/media-match-reviews/*/apply', async route => {
    if (!interrupted) {
      interrupted = true
      await route.fulfill({ status: 503, json: { detail: 'Temporary linking failure; admitted Release is retained.' } })
    } else await route.continue()
  })
  await matches.getByRole('button', { name: 'Accept selected (1)', exact: true }).click()
  await expect(matches).toContainText('Temporary linking failure; admitted Release is retained.')
  await expect(dialog).toContainText('Release is in the Catalog.')
  await matches.getByRole('button', { name: 'Retry same selected batch', exact: true }).click()
  await expect(matches.getByRole('alert').filter({ hasText: '1 copies linked; 0 classifications saved.' })).toBeVisible()
  await dialog.getByRole('button', { name: 'Close media admission', exact: true }).click()
  await expect(dialog).not.toBeVisible()
  await expect(page.getByRole('region', { name: 'Bulk media matching', exact: true })).toContainText('Already linked')
})

test('Media worklist ticks supported cross-copy proposals and accepts once without a typed reason', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 })
  await login(page)
  await page.getByLabel('Filter release', { exact: true }).fill('Worklist Fixture Release')
  await page.getByRole('button', { name: 'Search', exact: true }).click()
  const worklist = page.getByRole('region', { name: 'Bulk media matching', exact: true })
  const accept = worklist.getByRole('button', { name: 'Accept selected (0)', exact: true })
  await expect(accept).toBeDisabled()
  await worklist.getByRole('button', { name: 'Select supported proposals on this page', exact: true }).click()
  await expect(worklist).toContainText('2 selected files on this page')
  await expect(worklist.getByRole('checkbox', { name: 'Select media 202', exact: true })).toBeDisabled()
  await expect(page.getByLabel(/reason/i)).toHaveCount(0)
  const preparation = page.waitForRequest(request => request.url().endsWith('/media-match-reviews') && request.method() === 'POST')
  await worklist.getByRole('button', { name: 'Accept selected (2)', exact: true }).click()
  expect((await preparation).postDataJSON().selections).toEqual([
    { mediaId: 200, recordingId: 'release-recording-2', classification: null },
    { mediaId: 201, recordingId: 'release-recording-2', classification: null },
  ])
  await expect(worklist.getByRole('alert').filter({ hasText: '2 copies linked; 0 classifications saved.' })).toBeVisible()
  await expect(worklist).toContainText('No Releases were assigned or created.')
  await expect(worklist.getByText('Already linked', { exact: true })).toHaveCount(2)
  expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(390)
})

test('Unmatched bootleg classification is independent of Label and remains filterable', async ({ page }) => {
  await login(page)
  await page.getByLabel('Filter track', { exact: true }).fill('Worklist private bootleg')
  await page.getByRole('button', { name: 'Search', exact: true }).click()
  const worklist = page.getByRole('region', { name: 'Bulk media matching', exact: true })
  await expect(worklist.getByRole('checkbox', { name: 'Select media 202', exact: true })).toBeDisabled()
  await worklist.getByLabel('Classify selected media', { exact: true }).click()
  await page.getByRole('option', { name: 'Unofficial / self-released', exact: true }).click()
  await worklist.getByRole('checkbox', { name: 'Select media 202', exact: true }).check()
  await worklist.getByRole('button', { name: 'Accept selected (1)', exact: true }).click()
  await expect(worklist.getByRole('alert').filter({ hasText: '0 copies linked; 1 classifications saved.' })).toBeVisible()
  await expect(worklist.getByRole('table', { name: 'Media match proposals' }).getByText('Unofficial / self-released', { exact: true })).toBeVisible()
  await page.getByLabel('Media classification', { exact: true }).click()
  await page.getByRole('option', { name: 'Unofficial / self-released', exact: true }).click()
  await expect(worklist).toContainText('Worklist private bootleg')
  await expect(worklist).toContainText('Worklist Fixture Label')
  await expect(worklist).toContainText('No existing Catalog match')
})

test('An interrupted acceptance is recovered after reload using the saved batch, never silently accepted twice', async ({ page }) => {
  const media = { id: 42, title: 'Mock Track', mix: 'Original Mix', artist: 'Mock Artist', library: 'JetDrive', path: '/mock/track.flac' }
  const candidate = { recordingId: 'mock-recording', title: 'Mock Track', mix: 'Original Mix',
    status: 'Supported', evidence: ['Artist, title, explicit version and duration agree.'], blockers: [] }
  const row = { media, candidates: [candidate], suggestedRecordingId: candidate.recordingId, linkedRecordingId: null,
    alreadyRegistered: false, classification: 'unclassified' }
  const plan = { version: 'v1', files: [row] }
  const review = { id: 'mock-batch', version: 'review-v1', plan, selections: [{ mediaId: 42, recordingId: candidate.recordingId, classification: null }],
    verifiedMediaIds: [42], createdAt: '2026-11-09T00:00:00Z', outcome: null }
  let acceptances = 0
  await page.route('**/api/v1/media?*', route => route.fulfill({ json: {
    items: [media], total: 1, page: 1, pageSize: 25, libraries: ['JetDrive'], libraryNames: { JetDrive: 'Collection' }, warnings: [],
  } }))
  await page.route('**/api/v1/media-match-proposals', route => route.fulfill({ headers: { ETag: '"proposal"' }, json: plan }))
  await page.route('**/api/v1/media-match-reviews', async route => {
    expect(route.request().headers()['if-match']).toBe('"proposal"')
    await route.fulfill({ status: 201, headers: { ETag: '"frozen"' }, json: review })
  })
  await page.route('**/api/v1/media-match-reviews/mock-batch/apply', async route => {
    acceptances++
    expect(route.request().headers()['if-match']).toBe('"frozen"')
    expect(route.request().postDataJSON()).toEqual({ confirmed: true })
    await route.fulfill({ status: 503, json: { detail: 'Connection interrupted; check saved outcome.' } })
  })
  await page.route('**/api/v1/media-match-reviews/mock-batch', route => route.fulfill({ headers: { ETag: '"frozen"' }, json: {
    ...review, outcome: { reviewId: review.id, links: [{ recordingId: candidate.recordingId }], classifications: [], appliedAt: '2026-11-09T00:00:00Z' },
  } }))
  await login(page)
  const worklist = page.getByRole('region', { name: 'Bulk media matching', exact: true })
  await worklist.getByRole('button', { name: 'Select supported proposals on this page', exact: true }).click()
  await worklist.getByRole('button', { name: 'Accept selected (1)', exact: true }).click()
  await expect(worklist).toContainText('Connection interrupted; check saved outcome.')
  await expect(worklist.getByRole('button', { name: 'Check saved outcome', exact: true })).toBeVisible()
  await expect(page.getByLabel('Media library', { exact: true })).toHaveAttribute('aria-disabled', 'true')
  await page.reload()
  await page.getByLabel('API key').fill('test-only-browser-key-not-a-production-secret')
  await page.getByRole('button', { name: 'Connect', exact: true }).click()
  await expect(worklist.getByRole('alert').filter({ hasText: '1 copies linked; 0 classifications saved.' })).toBeVisible()
  expect(acceptances).toBe(1)
  expect(await page.evaluate(() => sessionStorage.getItem('trackstash.mediaMatches.pending'))).toBeNull()
})
