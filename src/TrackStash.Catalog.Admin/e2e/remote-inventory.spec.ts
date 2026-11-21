import { expect, test } from '@playwright/test'

test('device inventory supports searching, paging and matching selected suggestions', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 })
  let linked = false
  let matchBody: unknown
  await page.route('**/api/v1/remote-inventory/match-proposals', route => {
    const body = route.request().postDataJSON() as { items: { ownerId: string; deviceId: string; mediaFileId: number }[] }
    return route.fulfill({ json: body.items.map(key => ({
      key, version: 'a'.repeat(64), linkedRecordingId: linked ? 'recording-1' : null,
      suggestedRecordingId: linked ? null : 'recording-1',
      candidates: [{ recordingId: 'recording-1', title: 'Catalog tune', mix: null, status: 'Supported', evidence: [], blockers: [] }],
    })) })
  })
  await page.route('**/api/v1/remote-inventory/matches', route => {
    matchBody = route.request().postDataJSON()
    linked = true
    return route.fulfill({ json: { saved: 1, alreadyLinked: 0 } })
  })
  await page.route('**/api/v1/remote-inventory?*', route => {
    const query = new URL(route.request().url()).searchParams
    const filtered = query.get('q') === 'Example'
    const pageNumber = Number(query.get('page'))
    return route.fulfill({ json: {
      items: [{ ownerId: 'owner', deviceId: 'Mac', mediaFileId: pageNumber, path: '/music/label/release/example-track.flac',
        title: filtered ? 'Example tune' : `Track ${pageNumber}`, artist: 'Example artist',
        label: 'Example label', release: 'Example release', album: null, format: 'flac',
        scannedUtc: '2026-01-01T00:00:00Z' }],
      total: filtered ? 1 : 26, page: pageNumber, pageSize: 25,
    } })
  })
  await page.goto('/admin/#/remote-inventory')
  await page.getByLabel('API key').fill('test-only-browser-key-not-a-production-secret')
  await page.getByRole('button', { name: 'Connect', exact: true }).click()
  await expect(page.getByRole('table', { name: 'Device inventory' })).toContainText('Track 1')
  await expect(page.getByText('Files are not freshly verified', { exact: false })).toBeVisible()
  await page.getByRole('button', { name: 'Next', exact: true }).click()
  await expect(page.getByRole('table', { name: 'Device inventory' })).toContainText('Track 2')
  await page.getByLabel('Search title, artist, label or release').fill('Example')
  await page.getByRole('button', { name: 'Search', exact: true }).click()
  await expect(page.getByRole('table', { name: 'Device inventory' })).toContainText('Example tune')
  await expect(page.getByRole('button', { name: 'Next', exact: true })).toBeDisabled()
  await page.getByRole('button', { name: 'Select supported suggestions on this page' }).click()
  await page.getByRole('button', { name: 'Match selected' }).click()
  await expect(page.getByText('1 files matched; 0 were already linked.')).toBeVisible()
  await expect(page.getByRole('link', { name: 'Already matched' })).toBeVisible()
  expect(matchBody).toEqual({ items: [{
    key: { ownerId: 'owner', deviceId: 'Mac', mediaFileId: 1 }, version: 'a'.repeat(64), recordingId: 'recording-1',
  }] })
  expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(390)
})

test('matches supported suggestions across pages and allows choosing another recording', async ({ page }) => {
  const matched: number[] = []
  await page.route('**/api/v1/remote-inventory?*', route => {
    const number = Number(new URL(route.request().url()).searchParams.get('page'))
    return route.fulfill({ json: {
      items: [{ ownerId: 'owner', deviceId: 'Mac', mediaFileId: number, path: `/music/${number}.flac`,
        title: `Track ${number}`, artist: 'Artist', label: null, release: null, album: null, format: 'flac',
        scannedUtc: '2026-01-01T00:00:00Z' }],
      total: 26, page: number, pageSize: 25,
    } })
  })
  await page.route('**/api/v1/remote-inventory/match-proposals', route => {
    const body = route.request().postDataJSON() as { items: { ownerId: string; deviceId: string; mediaFileId: number }[] }
    return route.fulfill({ json: body.items.map(key => ({
      key, version: 'a'.repeat(64), linkedRecordingId: matched.includes(key.mediaFileId) ? 'recording-1' : null,
      suggestedRecordingId: key.mediaFileId === 1 && !matched.includes(1) ? 'recording-1' : null,
      candidates: key.mediaFileId === 1 ? [{ recordingId: 'recording-1', title: 'Catalog tune', mix: null,
        status: 'Supported', evidence: [], blockers: [] }] : [],
    })) })
  })
  await page.route('**/api/v1/remote-inventory/matches', route => {
    const body = route.request().postDataJSON() as { items: { key: { mediaFileId: number } }[] }
    matched.push(...body.items.map(item => item.key.mediaFileId))
    return route.fulfill({ json: { saved: body.items.length, alreadyLinked: 0 } })
  })
  await page.route('**/api/v1/tracks?*', route => route.fulfill({ json: {
    items: [{ recordingId: 'other', title: 'Other tune', mixName: null, recordingCredits: [] }], total: 1, page: 1, pageSize: 25,
  } }))
  await page.goto('/admin/#/remote-inventory')
  await page.getByLabel('API key').fill('test-only-browser-key-not-a-production-secret')
  await page.getByRole('button', { name: 'Connect', exact: true }).click()
  await page.getByRole('button', { name: 'Match supported suggestions across all results' }).click()
  await expect(page.getByText('1 files matched; 0 were already linked.')).toBeVisible()
  expect(matched).toEqual([1])
  await page.getByRole('button', { name: 'Next', exact: true }).click()
  await page.getByRole('button', { name: 'Find another Recording' }).click()
  await page.getByRole('button', { name: 'Search Recordings' }).click()
  await page.getByRole('button', { name: /Choose Other tune/ }).click()
  await page.getByRole('button', { name: 'Match selected' }).click()
  await expect.poll(() => matched).toEqual([1, 2])
})
