import { expect, test } from '@playwright/test'

const key = 'test-only-browser-key-not-a-production-secret'

for (const context of [
  { path: '/releases/release-fixture/show', label: 'Catalog release tracks' },
  { path: '/labels/release-fixture-label/show', label: 'Label tracks' },
  { path: '/artists/release-fixture-artist/show', label: 'Artist track credits' },
]) {
  test(`Shared ${context.label} row shows copy count and loads media only on expansion`, async ({ page }) => {
    let mediaReads = 0
    await page.route('**/api/v1/tracks?*', async route => {
      const response = await route.fetch()
      const data = await response.json()
      for (const track of data.items) {
        track.linkedCopyCount = 2
        track.musicalMetadata = { bpm: 124, keyValue: '8A', keyNotation: 'Camelot' }
      }
      await route.fulfill({ response, json: data })
    })
    await page.route('**/api/v1/recordings/*/media', async route => {
      mediaReads++
      await route.fulfill({ json: [{ id: 'test-copy', path: '/hidden/path/tune.flac', libraryName: 'DJ library', format: 'FLAC', fingerprintKey: 'audio-key' }] })
    })
    await page.goto(`/admin/#${context.path}`)
    await page.getByLabel('API key').fill(key)
    await page.getByRole('button', { name: 'Connect', exact: true }).click()
    if (context.label !== 'Catalog release tracks') await page.getByRole('tab', { name: 'Tracks', exact: true }).click()
    const tracks = page.getByRole('table', { name: context.label, exact: true })
    await expect(tracks).toBeVisible()
    expect(mediaReads).toBe(0)
    const row = tracks.getByRole('row').nth(1)
    await expect(row).toContainText('124')
    await expect(row).toContainText('8A (Camelot)')
    const title = await row.getByRole('link', { name: /^Catalog Track/ }).first().textContent()
    await row.getByRole('button', { name: `Show media and track details for ${title}`, exact: true }).click()
    const detail = tracks.getByRole('region', { name: `Expanded track ${title}`, exact: true })
    await expect(detail).toContainText('DJ library')
    await expect(detail.getByText('/hidden/path/tune.flac', { exact: true })).not.toBeVisible()
    expect(mediaReads).toBe(1)
    await row.getByRole('button', { name: `Hide media and track details for ${title}`, exact: true }).click()
    await expect(detail).toHaveCount(0)
  })
}

test('Tracks menu, Recording page and Label tracks share one read-only track list', async ({ page }) => {
  const writes: string[] = []
  const errors: string[] = []
  page.on('request', request => { if (request.url().includes('/api/v1/') && request.method() !== 'GET') writes.push(request.method()) })
  page.on('pageerror', error => errors.push(error.message))
  await page.goto('/admin/#/tracks')
  await page.getByLabel('API key').fill(key)
  await page.getByRole('button', { name: 'Connect', exact: true }).click()
  await expect(page.getByRole('heading', { name: 'Tracks', exact: true, level: 1 })).toBeVisible()
  const list = page.getByRole('table', { name: 'Catalog tracks', exact: true })
  await expect(list.getByRole('columnheader')).toHaveText(['Details', '#', 'Title / Artist · Remixer', 'Release', 'Label / Genre · Subgenre', 'Release date', 'BPM', 'Key', 'Length', 'Copies', 'Releases'])
  const search = page.getByRole('textbox', { name: 'Search tracks', exact: true })
  expect((await search.boundingBox())!.width).toBeGreaterThan(300)
  await search.fill('RELTEST25')
  await expect(list.getByRole('row')).toHaveCount(2)
  await expect(page).toHaveURL(/q=RELTEST25/)
  const row = list.getByRole('row').nth(1)
  await expect(row.getByRole('cell').nth(3)).toHaveText('Fixture Catalog Release')
  await expect(row.getByRole('cell').nth(8)).toHaveText('2:03')
  await expect(row.getByRole('cell').nth(9)).toHaveText('0')
  await expect(row.getByRole('cell').nth(10)).toHaveText('1')
  await page.getByRole('combobox', { name: 'Sort tracks', exact: true }).click()
  await expect(page.getByRole('option')).toHaveText(['Newest release first', 'Oldest release first', 'Title A–Z', 'Artist A–Z', 'Release A–Z'])
  await page.getByRole('option', { name: 'Title A–Z', exact: true }).click()
  await expect(page).toHaveURL(/sort=title/)
  await row.getByRole('link', { name: 'Catalog Track 26', exact: true }).click()
  await expect(page).toHaveURL(/releaseId=release-fixture/)
  await expect(page.getByRole('heading', { name: 'Catalog Track 26 Original Mix', exact: true, level: 1 })).toBeVisible()
  await expect(page.getByText('ISRC: RELTEST25; length: 2:03; releases: 1', { exact: true })).toBeVisible()
  const releases = page.getByRole('table', { name: 'Recording releases', exact: true })
  await expect(releases.getByRole('row')).toHaveCount(2)
  await expect(releases.getByRole('link', { name: 'Catalog Track 26', exact: true })).toHaveCount(0)
  await releases.getByRole('button', { name: 'Show media and track details for Catalog Track 26', exact: true }).click()
  await releases.getByRole('button', { name: 'Catalog credits and track details', exact: true }).click()
  await expect(releases).toContainText('Placement ID: release-placement-25')
  await releases.getByRole('link', { name: 'Release Fixture Label', exact: true }).click()
  await page.getByRole('tab', { name: 'Tracks', exact: true }).click()
  const labelTracks = page.getByRole('table', { name: 'Label tracks', exact: true })
  await expect(labelTracks.getByRole('row')).toHaveCount(26)
  await page.getByRole('textbox', { name: 'Search tracks', exact: true }).fill('fixture remix')
  await expect(labelTracks.getByRole('row')).toHaveCount(2)
  await expect(labelTracks).toContainText('Catalog Track 1 Fixture Remix')
  await page.getByRole('textbox', { name: 'Search tracks', exact: true }).fill('no such track')
  await expect(page.getByText('No tracks match this search.', { exact: true })).toBeVisible()
  await page.getByRole('menuitem', { name: 'Tracks', exact: true }).click()
  await expect(page.getByRole('heading', { name: 'Tracks', exact: true, level: 1 })).toBeVisible()
  expect(writes).toEqual([]); expect(errors).toEqual([])
})

for (const narrow of [false, true]) {
test(`Recording hero retains the release cover and thumbnail on ${narrow ? 'narrow' : 'desktop'} screens`, async ({ page }) => {
  if (narrow) await page.setViewportSize({ width: 390, height: 844 })
  const assetId = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'
  const bytes = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aX1cAAAAASUVORK5CYII=', 'base64')
  const queries: string[] = []
  await page.route('**/api/v1/tracks?*', async route => {
    const url = new URL(route.request().url())
    if (url.searchParams.get('recordingId') !== 'release-recording-0') return route.continue()
    if (url.searchParams.get('pageSize') === '1') {
      queries.push(url.search)
      expect(url.searchParams.get('releaseId')).toBe('release-fixture')
      expect(url.searchParams.get('groupBy')).toBe('recording')
    }
    const response = await route.fetch()
    const data = await response.json()
    data.items[0].coverAssetId = assetId
    await route.fulfill({ response, json: data })
  })
  await page.route(`**/api/v1/artwork-assets/${assetId}/**`, route => route.fulfill({ contentType: 'image/png', body: bytes }))
  await page.goto('/admin/#/recordings/release-recording-0/show?releaseId=release-fixture')
  await page.getByLabel('API key').fill(key)
  await page.getByRole('button', { name: 'Connect', exact: true }).click()
  const hero = page.getByRole('region', { name: 'Recording release artwork' })
  const image = hero.getByRole('img', { name: 'Fixture Catalog Release cover for this Recording' })
  await expect(image).toHaveAttribute('src', /^blob:/)
  await expect(image).toHaveCSS('width', '240px')
  await expect(hero.getByRole('link', { name: 'View artwork release: Fixture Catalog Release' }))
    .toHaveAttribute('href', '#/releases/release-fixture/show')
  const title = page.getByRole('heading', { level: 1 })
  const artworkBox = (await image.boundingBox())!
  const titleBox = (await title.boundingBox())!
  if (narrow) expect(artworkBox.y + artworkBox.height).toBeLessThanOrEqual(titleBox.y)
  else expect(artworkBox.x + artworkBox.width).toBeLessThanOrEqual(titleBox.x)
  const releases = page.getByRole('table', { name: 'Recording releases', exact: true })
  await expect(releases).toBeVisible()
  await expect(releases.getByRole('img', { name: 'Fixture Catalog Release cover' })).toHaveAttribute('src', /^blob:/)
  expect(artworkBox.y).toBeLessThan((await releases.boundingBox())!.y)
  expect(queries).toHaveLength(1)
  await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true)
  await expect(page.getByRole('button', { name: /play audio/i })).toHaveCount(0)
})
}

test('Recording artwork defaults to newest release and does not borrow artwork for invalid release context', async ({ page }) => {
  await page.goto('/admin/#/recordings/release-recording-0/show')
  await page.getByLabel('API key').fill(key)
  await page.getByRole('button', { name: 'Connect', exact: true }).click()
  const hero = page.getByRole('region', { name: 'Recording release artwork' })
  await expect(hero).toContainText('Newest release:')
  await expect(hero).toContainText('No preferred release cover')
  await page.goto('/admin/#/recordings/release-recording-0/show?releaseId=missing')
  await expect(hero.getByRole('alert')).toContainText('Catalog release was not found.')
  await expect(hero.getByRole('img')).toHaveCount(0)
})

test('Recording hero changes release identity with context and handles an unplaced Recording', async ({ page }) => {
  let unplaced = false
  await page.route('**/api/v1/tracks?*', async route => {
    const url = new URL(route.request().url())
    if (url.searchParams.get('pageSize') !== '1') return route.continue()
    const response = await route.fetch({ url: `${url.origin}${url.pathname}?recordingId=release-recording-0&pageSize=1&groupBy=recording` })
    const data = await response.json()
    const context = url.searchParams.get('releaseId')
    if (context === 'unrelated') data.items = []
    else if (unplaced) Object.assign(data.items[0], { releaseId: null, releaseTitle: null, coverAssetId: null })
    else if (!context) Object.assign(data.items[0], { releaseId: 'newest-release', releaseTitle: 'Newest compilation' })
    await route.fulfill({ response, json: data })
  })
  await page.goto('/admin/#/recordings/release-recording-0/show')
  await page.getByLabel('API key').fill(key)
  await page.getByRole('button', { name: 'Connect', exact: true }).click()
  const hero = page.getByRole('region', { name: 'Recording release artwork' })
  await expect(hero.getByRole('link', { name: 'Newest compilation' })).toHaveAttribute('href', '#/releases/newest-release/show')
  await page.goto('/admin/#/recordings/release-recording-0/show?releaseId=release-fixture')
  await expect(hero).toContainText('Artwork from:')
  await expect(hero.getByRole('link', { name: 'Fixture Catalog Release' })).toHaveAttribute('href', '#/releases/release-fixture/show')
  await expect(hero.getByRole('link', { name: 'Newest compilation' })).toHaveCount(0)
  await page.goto('/admin/#/recordings/release-recording-0/show?releaseId=unrelated')
  await expect(hero).toContainText('This Recording is not listed on the requested release.')
  unplaced = true
  await page.goto('/admin/#/recordings/release-recording-0/show')
  await expect(hero).toContainText('No release artwork available')
  await expect(hero.getByRole('link')).toHaveCount(0)
})

test('Recording hero artwork lookup failure is explicit without hiding the Recording', async ({ page }) => {
  await page.route('**/api/v1/tracks?*', route => {
    if (new URL(route.request().url()).searchParams.get('pageSize') !== '1') return route.continue()
    return route.fulfill({ status: 503, json: { detail: 'Release artwork lookup unavailable.' } })
  })
  await page.goto('/admin/#/recordings/release-recording-0/show')
  await page.getByLabel('API key').fill(key)
  await page.getByRole('button', { name: 'Connect', exact: true }).click()
  await expect(page.getByRole('region', { name: 'Recording release artwork' }).getByRole('alert'))
    .toContainText('Release artwork lookup unavailable.')
  await expect(page.getByRole('heading', { level: 1 })).toContainText('Catalog Track 1')
  await expect(page.getByRole('table', { name: 'Recording releases', exact: true })).toBeVisible()
})

test('missing Recording shows an explicit error', async ({ page }) => {
  await page.goto('/admin/#/recordings/missing/show')
  await page.getByLabel('API key').fill(key)
  await page.getByRole('button', { name: 'Connect', exact: true }).click()
  await expect(page.getByRole('alert').filter({ hasText: 'Catalog Recording was not found.' })).toBeVisible()
  await expect(page.getByRole('table', { name: 'Recording releases', exact: true })).toHaveCount(0)
})

test('track search and sort stay usable on a narrow touch screen', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 })
  await page.goto('/admin/#/tracks')
  await page.getByLabel('API key').fill(key)
  await page.getByRole('button', { name: 'Connect', exact: true }).click()
  const search = (await page.getByRole('textbox', { name: 'Search tracks', exact: true }).boundingBox())!
  const sort = (await page.getByRole('combobox', { name: 'Sort tracks', exact: true }).boundingBox())!
  expect(search.width).toBeGreaterThan(250); expect(search.height).toBeLessThan(80)
  expect(sort.width).toBeGreaterThan(250)
  expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(390)
})
