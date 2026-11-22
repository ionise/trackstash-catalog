import { expect, test } from '@playwright/test'

test('unmatched device files can create a pseudo release without a Recording suggestion', async ({ page }) => {
  const key = { ownerId: 'owner', deviceId: 'Mac', mediaFileId: 1 }
  const version = 'a'.repeat(64)
  let submitted: unknown
  await page.route('**/api/v1/remote-inventory?*', route => route.fulfill({ json: {
    items: [{ ...key, path: '/offline/file.flac', title: 'Bootleg', artist: 'Artist', label: null, release: null,
      album: null, format: 'flac', scannedUtc: '2026-01-01T00:00:00Z' }],
    total: 1, page: 1, pageSize: 25, devices: ['Mac'],
  } }))
  await page.route('**/api/v1/remote-inventory/match-proposals', route => route.fulfill({ json: [
    { key, version, linkedRecordingId: null, suggestedRecordingId: null, candidates: [] },
  ] }))
  await page.route('**/api/v1/remote-inventory/pseudo-releases', route => {
    submitted = route.request().postDataJSON()
    return route.fulfill({ status: 201, json: { operationId: 'operation', releaseId: 'pseudo', recordings: 1, mediaFiles: 1 } })
  })
  await page.goto('/admin/#/remote-inventory')
  await page.getByLabel('API key').fill('test-only-browser-key-not-a-production-secret')
  await page.getByRole('button', { name: 'Connect', exact: true }).click()
  await page.getByRole('checkbox', { name: 'Pseudo media 1' }).check()
  await page.getByRole('button', { name: 'Create pseudo release from 1 selected device files' }).click()
  const dialog = page.getByRole('dialog', { name: 'Create pseudo release for device media' })
  await expect(dialog).toContainText('Device files and audio are not freshly verified')
  await dialog.getByLabel('Device pseudo release title').fill('My bootlegs')
  await dialog.getByRole('button', { name: 'Create device pseudo release and link records' }).click()
  await expect(dialog.getByRole('link', { name: 'View pseudo release' })).toBeVisible()
  expect(submitted).toMatchObject({ title: 'My bootlegs', catalogNumber: 'NO CAT',
    items: [{ key, version, title: 'Bootleg', newArtist: 'Artist', recordingId: null }] })
})

test('device media provider search keeps its scanner snapshot and opens the reported Release', async ({ page }) => {
  const key = { ownerId: 'owner', deviceId: 'Mac', mediaFileId: 1 }
  const version = 'a'.repeat(64)
  let lookupBody: unknown
  await page.route('**/api/v1/remote-inventory?*', route => route.fulfill({ json: {
    items: [{ ...key, path: '/offline/file.flac', title: 'Tune', artist: 'Artist', label: null, release: null,
      album: null, format: 'flac', scannedUtc: '2026-01-01T00:00:00Z' }],
    total: 1, page: 1, pageSize: 25, devices: ['Mac'],
  } }))
  await page.route('**/api/v1/remote-inventory/match-proposals', route => route.fulfill({ json: [
    { key, version, linkedRecordingId: null, suggestedRecordingId: null, candidates: [] },
  ] }))
  await page.route('**/api/v1/remote-inventory/provider-lookup/beatport', route => {
    lookupBody = route.request().postDataJSON()
    return route.fulfill({ json: { providerId: 'beatport', query: { isrc: 'GBABC2600001' },
      candidates: [{ candidateId: '7', match: { conflicts: ['Check version'] },
        metadata: { name: 'Tune', mixName: 'Dub', isrc: 'GBABC2600001', bpm: 120, musicalKey: null, credits: [] } }], warnings: [] } })
  })
  await page.route('**/api/v1/remote-inventory/provider-lookup/sonovault', route => route.fulfill({ json: {
    providerId: 'sonovault', query: { isrc: 'GBABC2600001' }, candidates: [{ candidateId: '17',
      match: { conflicts: [] }, reportedRelease: { source: 'sonovault', id: '18', title: 'Representative' },
      metadata: { name: 'Tune', mixName: null, isrc: 'GBABC2600001', bpm: null, musicalKey: null, credits: [] } }], warnings: [],
  } }))
  await page.route('**/api/v1/media/provider-release/sonovault/18*', route => route.fulfill({
    json: route.request().url().includes('editionId=19') ? { candidates: [], warnings: [] } : {
      candidates: [{ candidateId: '18', metadata: { name: 'Representative', releaseDetails: {
        editions: [{ id: '19', source: 'discogs', title: 'Edition', labelName: 'Label', catalogNumber: 'CAT 42', releaseDate: null }],
        tracks: [{ id: '17', discNumber: 1, trackNumber: 2, title: 'Tune', isrc: 'GBABC2600001' }],
      } } }], warnings: [],
    },
  }))
  await page.route('**/api/v1/beatport/tracks/7/release', route => route.fulfill({ json: {
    release: { id: 10, name: 'Example Release', label: { name: 'Label' }, catalogReleaseIds: ['release-1'] },
    tracks: [{ id: 7, name: 'Tune', mixName: 'Dub', isrc: 'GBABC2600001' }], trackTotal: 1,
  } }))
  await page.goto('/admin/#/remote-inventory')
  await page.getByLabel('API key').fill('test-only-browser-key-not-a-production-secret')
  await page.getByRole('button', { name: 'Connect', exact: true }).click()
  await page.getByRole('button', { name: 'Find provider metadata for media 1' }).click()
  const dialog = page.getByRole('dialog', { name: 'Find a Release for device media' })
  await dialog.getByRole('button', { name: 'Search SonoVault', exact: true }).click()
  await dialog.getByRole('button', { name: 'Inspect SonoVault Release Representative' }).click()
  await expect(dialog.getByRole('region', { name: 'SonoVault Release evidence' })).toContainText('CAT 42')
  await expect(dialog.getByRole('region', { name: 'SonoVault Release evidence' })).toContainText('1 / 2: Tune')
  await dialog.getByRole('button', { name: 'Inspect edition 19' }).click()
  await expect(dialog.getByText('That SonoVault Release is no longer available.')).toBeVisible()
  await expect(dialog.getByText('CAT 42')).not.toBeVisible()
  await dialog.getByRole('button', { name: 'Search Beatport', exact: true }).click()
  await expect(dialog.getByText('Check version')).toBeVisible()
  await dialog.getByRole('button', { name: 'Find Release for Tune' }).click()
  await expect(dialog.getByText('Release is in the Catalog.', { exact: false })).toBeVisible()
  expect(lookupBody).toEqual({ key, version, searchByMetadata: false })
  await dialog.getByRole('button', { name: 'Choose catalog Recording' }).click()
  await expect(page.getByRole('dialog', { name: 'Choose a catalog Recording' })).toBeVisible()
})

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
        scannedUtc: '2026-01-01T00:00:00Z', isrc: 'GBTRK0000001', durationSeconds: 300, sizeBytes: 1234,
        tags: { catalogNumber: 'CAT 42', trackNumber: '2/11', bpm: '120 BPM', musicalKey: 'A minor' } }],
      total: filtered ? 1 : 26, page: pageNumber, pageSize: 25,
    } })
  })
  await page.goto('/admin/#/remote-inventory')
  await page.getByLabel('API key').fill('test-only-browser-key-not-a-production-secret')
  await page.getByRole('button', { name: 'Connect', exact: true }).click()
  await expect(page.getByRole('table', { name: 'Device inventory' })).toContainText('Track 1')
  await page.getByText('File metadata 1').click()
  await expect(page.getByText('CAT 42')).toBeVisible()
  await expect(page.getByText('2/11')).toBeVisible()
  await expect(page.getByText('GBTRK0000001')).toBeVisible()
  await page.getByText('File metadata 1').click()
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

test('bulk matching snapshots unmatched pages before linking', async ({ page }) => {
  const matched = new Set<number>()
  await page.route('**/api/v1/remote-inventory?*', route => {
    const query = new URL(route.request().url()).searchParams
    const number = Number(query.get('page'))
    const remaining = Array.from({ length: 30 }, (_, index) => index + 1)
      .filter(id => query.get('match') !== 'unmatched' || !matched.has(id))
    return route.fulfill({ json: {
      items: remaining.slice((number - 1) * 25, number * 25).map(id => ({
        ownerId: 'owner', deviceId: 'Mac', mediaFileId: id, path: `/music/${id}.flac`,
        title: `Track ${id}`, artist: 'Artist', label: null, release: null, album: null, format: 'flac',
        scannedUtc: '2026-01-01T00:00:00Z', version: 'a'.repeat(64),
      })),
      total: remaining.length, page: number, pageSize: 25, devices: ['Mac'],
    } })
  })
  await page.route('**/api/v1/remote-inventory/match-proposals', route => {
    const body = route.request().postDataJSON() as { items: { ownerId: string; deviceId: string; mediaFileId: number }[] }
    return route.fulfill({ json: body.items.map(key => ({
      key, version: 'a'.repeat(64), linkedRecordingId: null, suggestedRecordingId: 'recording-1', candidates: [],
    })) })
  })
  await page.route('**/api/v1/remote-inventory/matches', route => {
    const body = route.request().postDataJSON() as { items: { key: { mediaFileId: number } }[] }
    body.items.forEach(item => matched.add(item.key.mediaFileId))
    return route.fulfill({ json: { saved: body.items.length, alreadyLinked: 0 } })
  })
  await page.goto('/admin/#/remote-inventory')
  await page.getByLabel('API key').fill('test-only-browser-key-not-a-production-secret')
  await page.getByRole('button', { name: 'Connect', exact: true }).click()
  await page.getByLabel('Match status').click()
  await page.getByRole('option', { name: 'Unmatched', exact: true }).click()
  await page.getByRole('button', { name: 'Match supported suggestions across all results' }).click()
  await expect(page.getByText('30 files matched; 0 were already linked.')).toBeVisible()
  expect([...matched].sort((a, b) => a - b)).toEqual(Array.from({ length: 30 }, (_, index) => index + 1))
})

test('release searches unmatched device media and links selected recordings with snapshot checks', async ({ page }) => {
  const requests: unknown[] = []
  let stale = true
  await page.route('**/api/v1/tracks?*', route => {
    const query = new URL(route.request().url()).searchParams
    if (query.get('media') !== 'none') return route.continue()
    expect(query.get('releaseId')).toBe('release-fixture')
    return route.fulfill({ json: { items: [
      { recordingId: 'missing-1', title: 'First missing tune', mixName: null },
      { recordingId: 'missing-2', title: 'Second missing tune', mixName: 'Club mix' },
    ], total: 2, page: 1, pageSize: 100 } })
  })
  await page.route('**/api/v1/remote-inventory?*', route => {
    const query = new URL(route.request().url()).searchParams
    expect(query.get('match')).toBe('unmatched')
    const number = Number(query.get('page'))
    return route.fulfill({ json: {
      items: [{ ownerId: 'owner', deviceId: 'Mac', mediaFileId: number, path: `/music/${number}.flac`,
        title: `File ${number}`, artist: 'Artist', label: null, release: 'Fixture Catalog Release', album: null, format: 'flac',
        scannedUtc: '2026-01-01T00:00:00Z', version: 'a'.repeat(64) }],
      total: 26, page: number, pageSize: 25, devices: ['Mac'],
    } })
  })
  await page.route('**/api/v1/remote-inventory/match-proposals', route => {
    const body = route.request().postDataJSON() as { items: { ownerId: string; deviceId: string; mediaFileId: number }[] }
    return route.fulfill({ json: body.items.map(key => ({
      key, version: 'a'.repeat(64), linkedRecordingId: null, suggestedRecordingId: 'missing-1',
      candidates: [{ recordingId: 'missing-1', title: 'First missing tune', mix: null,
        status: 'Supported', evidence: ['Matching title'], blockers: [] }],
    })) })
  })
  await page.route('**/api/v1/remote-inventory/matches', route => {
    requests.push(route.request().postDataJSON())
    if (stale) {
      stale = false
      return route.fulfill({ status: 409, contentType: 'application/problem+json',
        body: JSON.stringify({ title: 'Scanner metadata changed; reload the matching suggestions.', status: 409 }) })
    }
    return route.fulfill({ json: { saved: 1, alreadyLinked: 0 } })
  })
  await page.goto('/admin/#/releases/release-fixture/show')
  await page.getByLabel('API key').fill('test-only-browser-key-not-a-production-secret')
  await page.getByRole('button', { name: 'Connect', exact: true }).click()
  await page.getByRole('button', { name: 'Find unmatched device media for this release' }).click()
  const table = page.getByRole('table', { name: 'Unmatched device media for release' })
  await expect(table).toContainText('File 1')
  await expect(table).toContainText('Matching title')
  await page.getByRole('button', { name: 'Select supported suggestions on this page' }).click()
  await page.getByRole('button', { name: 'Link selected media' }).click()
  await expect(page.getByRole('alert').filter({ hasText: 'Scanner metadata changed' })).toBeVisible()
  await page.getByRole('button', { name: 'Reload', exact: true }).click()
  await expect(table).toContainText('File 1')
  await page.getByRole('button', { name: 'Next media' }).click()
  await expect(table).toContainText('File 2')
  await page.getByLabel('Recording for media 2').click()
  await page.getByRole('option', { name: 'Second missing tune Club mix' }).click()
  await page.getByRole('button', { name: 'Link selected media' }).click()
  await expect(page.getByText('1 files linked; 0 were already linked.')).toBeVisible()
  expect(requests).toEqual([
    { items: [{ key: { ownerId: 'owner', deviceId: 'Mac', mediaFileId: 1 }, version: 'a'.repeat(64), recordingId: 'missing-1' }] },
    { items: [{ key: { ownerId: 'owner', deviceId: 'Mac', mediaFileId: 2 }, version: 'a'.repeat(64), recordingId: 'missing-2' }] },
  ])
})
