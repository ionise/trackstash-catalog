import { expect, test } from '@playwright/test'

const key = 'test-only-browser-key-not-a-production-secret'

test('Artist menu, label credits, linked Release credits and paged track appearances are read-only', async ({ page }) => {
  const writes: string[] = []
  const errors: string[] = []
  page.on('request', r => { if (r.url().includes('/api/v1/') && r.method() !== 'GET') writes.push(r.method()) })
  page.on('pageerror', e => errors.push(e.message))
  await page.goto('/admin/#/artists')
  await page.getByLabel('API key').fill(key)
  await page.getByRole('button', { name: 'Connect', exact: true }).click()
  await expect(page.getByRole('heading', { name: 'Artists', level: 1, exact: true })).toBeVisible()
  await page.getByRole('textbox', { name: 'Search artists', exact: true }).fill('Fixture Composer')
  const list = page.getByRole('table', { name: 'Catalog artists', exact: true })
  await expect(list.getByRole('row')).toHaveCount(2)
  await list.getByRole('link', { name: 'Fixture Composer', exact: true }).click()
  await expect(page.getByRole('heading', { name: 'Fixture Composer', level: 1, exact: true })).toBeVisible()
  await expect(page.getByRole('tab')).toHaveText(['Featured', 'Tracks', 'Releases', 'Associations', 'Details'])
  await page.getByRole('tab', { name: 'Tracks', exact: true }).click()
  const tracks = page.getByRole('table', { name: 'Artist track credits', exact: true })
  await expect(tracks.getByRole('row')).toHaveCount(26)
  await expect(tracks.getByRole('columnheader')).toHaveText(['#', 'Title / Artist · Remixer', 'Release', 'Label / Genre · Subgenre', 'Release date', 'BPM', 'Key', 'Length'])
  const firstTrack = tracks.getByRole('row').nth(1)
  await expect(firstTrack.getByRole('cell').nth(0)).toHaveText('1')
  await expect(firstTrack).toContainText('Catalog Track 1 Fixture Remix')
  await expect(firstTrack).toContainText('Genre not available')
  await expect(firstTrack.getByRole('cell').nth(2)).toHaveText('Fixture Catalog Release')
  await expect(firstTrack.getByRole('cell').nth(4)).toHaveText('2020-01-01')
  await expect(firstTrack.getByRole('cell').nth(5)).toHaveText('—')
  await expect(firstTrack.getByRole('cell').nth(6)).toHaveText('—')
  await expect(firstTrack.getByRole('link', { name: 'Release Fixture Label', exact: true })).toBeVisible()
  await expect(firstTrack.getByRole('link', { name: 'Fixture Composer', exact: true })).toHaveCount(1)
  await expect(tracks.getByLabel(/Track listing credit:/)).toHaveCount(25)
  await expect(tracks.getByLabel(/Recording credit:/)).toHaveCount(25)
  await page.getByRole('button', { name: 'Next page', exact: true }).last().click()
  await expect(tracks.getByRole('row')).toHaveCount(2)
  await expect(tracks.getByRole('row').nth(1).getByRole('cell').nth(0)).toHaveText('26')
  await page.getByRole('tab', { name: 'Releases', exact: true }).click()
  const releases = page.getByRole('table', { name: 'Artist release credits', exact: true })
  await expect(releases.getByRole('row')).toHaveCount(2)
  await expect(releases.getByRole('columnheader')).toHaveText(['#', 'Title / Artist', 'Label', 'Release date', 'BPM'])
  await expect(releases.getByRole('row').nth(1).getByRole('cell').nth(3)).toHaveText('2020-01-01')
  await expect(releases.getByLabel(/Release credit:/)).toHaveCount(1)
  await expect(releases.getByLabel(/Recording credit:/)).toHaveCount(1)
  await expect(releases.getByLabel(/Track listing credit:/)).toHaveCount(1)
  await expect(releases.getByRole('link', { name: 'Release Fixture Label', exact: true })).toHaveAttribute('href', '#/labels/release-fixture-label/show')
  await releases.getByRole('link', { name: 'Fixture Catalog Release', exact: true }).first().click()
  await expect(page.getByRole('heading', { name: 'Fixture Catalog Release', level: 1, exact: true })).toBeVisible()
  await page.getByRole('table', { name: 'Catalog release tracks' }).getByRole('link', { name: 'Fixture Remix Act', exact: true }).click()
  await expect(page.getByRole('heading', { name: 'Fixture Remix Act', level: 1, exact: true })).toBeVisible()
  await expect(page.getByRole('alert').filter({ hasText: 'unresolved compound source identity' })).toBeVisible()
  await page.getByRole('tab', { name: 'Tracks', exact: true }).click()
  const remixTrack = page.getByRole('table', { name: 'Artist track credits' }).getByRole('row').nth(1)
  await expect(remixTrack.getByRole('link', { name: 'Fixture Composer', exact: true })).toBeVisible()
  await expect(remixTrack).not.toContainText('Primary Artist not recorded')
  await expect(remixTrack.getByRole('cell').nth(4)).toHaveText('2020-01-01')
  await page.getByRole('tab', { name: 'Releases', exact: true }).click()
  await page.getByRole('table', { name: 'Artist release credits' }).getByRole('link', { name: 'Fixture Catalog Release', exact: true }).click()
  await page.getByRole('link', { name: 'Release Fixture Label (primary label)', exact: true }).click()
  await page.getByRole('tab', { name: 'Artists', exact: true }).click()
  const labelArtists = page.getByRole('table', { name: 'Label artists', exact: true })
  await expect(labelArtists.getByRole('row')).toHaveCount(3)
  await expect(page.getByText(/Credits do not establish that an Artist is signed/)).toBeVisible()
  await page.getByRole('combobox', { name: 'Credited role', exact: true }).click()
  await page.getByRole('option', { name: 'Remixer', exact: true }).click()
  await expect(labelArtists.getByRole('row')).toHaveCount(2)
  await expect(labelArtists).toContainText('Fixture Remix Act')
  await page.getByRole('textbox', { name: 'Search label artists', exact: true }).fill('no match')
  await expect(page.getByText('No catalog Artists match this view.', { exact: true })).toBeVisible()
  await page.getByRole('menuitem', { name: 'Artists', exact: true }).click()
  await expect(page.getByRole('heading', { name: 'Artists', level: 1, exact: true })).toBeVisible()
  expect(writes).toEqual([]); expect(errors).toEqual([])
})

test('Artist layout renders supplied metadata and collaborators without requiring backfill', async ({ page }) => {
  const composer = { artistId: 'release-fixture-artist', artistName: 'Fixture Composer', creditedName: 'Fixture Composer', role: 'primary', position: 1, identityStatus: 'accepted' }
  const remixer = { artistId: 'release-fixture-remixer', artistName: 'Fixture Remix Act', creditedName: 'Fixture Remix Act', role: 'remixer', position: 1, identityStatus: 'unresolved_compound' }
  await page.route(/\/api\/v1\/tracks\?/, async route => {
    const response = await route.fetch()
    const data = await response.json()
    await route.fulfill({ response, json: { ...data, items: data.items.map((item: object) => ({
      ...item, publishDate: '2026-11-05', genre: 'Progressive House', subgenre: 'Melodic', bpm: 120, keyName: 'G Minor',
      credits: [composer], recordingCredits: [remixer],
    })) } })
  })
  await page.route(/\/api\/v1\/artists\/release-fixture-artist\/release-appearances\?/, async route => {
    const response = await route.fetch()
    const data = await response.json()
    await route.fulfill({ response, json: { ...data, items: data.items.map((item: object) => ({
      ...item, publishDate: '2026-11-05', genre: 'Progressive House', subgenre: 'Melodic', bpm: 120, bpmMax: 128, keyName: 'G Minor',
      contributors: [composer, remixer],
    })) } })
  })
  await page.goto('/admin/#/artists/release-fixture-artist/show?tab=Tracks')
  await page.getByLabel('API key').fill(key)
  await page.getByRole('button', { name: 'Connect', exact: true }).click()
  const tracks = page.getByRole('table', { name: 'Artist track credits', exact: true })
  const track = tracks.getByRole('row').nth(1)
  await expect(track).toContainText('Fixture Composer · Fixture Remix Act [unresolved compound]')
  await expect(track).toContainText('Progressive House / Melodic')
  await expect(track.getByRole('cell').nth(4)).toHaveText('2026-11-05')
  await expect(track.getByRole('cell').nth(5)).toHaveText('120')
  await expect(track.getByRole('cell').nth(6)).toHaveText('G Minor')
  await page.getByRole('tab', { name: 'Releases', exact: true }).click()
  const release = page.getByRole('table', { name: 'Artist release credits', exact: true }).getByRole('row').nth(1)
  await expect(release.getByRole('cell').nth(3)).toHaveText('2026-11-05')
  await expect(release.getByRole('cell').nth(4)).toHaveText('120–128')
  await expect(release.getByRole('link', { name: 'Fixture Remix Act', exact: true })).toBeVisible()
})

test('missing Artist has an explicit error and no success-shaped detail', async ({ page }) => {
  await page.goto('/admin/#/artists/missing/show')
  await page.getByLabel('API key').fill(key)
  await page.getByRole('button', { name: 'Connect', exact: true }).click()
  await expect(page.getByRole('alert').filter({ hasText: 'Catalog Artist was not found.' })).toBeVisible()
  await expect(page.getByRole('heading', { name: 'Track credits', exact: true })).toHaveCount(0)
})

test('Artist tracks use the alternate release date and never borrow viewed-Artist evidence as contributors', async ({ page }) => {
  await page.route(/\/api\/v1\/tracks\?/, async route => {
    const response = await route.fetch()
    const data = await response.json()
    await route.fulfill({ response, json: { ...data, total: 2, items: [
      { ...data.items[0], publishDate: null, newReleaseDate: '2020-01-02', credits: [], recordingCredits: [] },
      { ...data.items[1], placementId: null, releaseId: null, releaseTitle: null, labels: [],
        publishDate: null, newReleaseDate: null, credits: [], recordingCredits: [] },
    ] } })
  })
  await page.goto('/admin/#/artists/release-fixture-artist/show?tab=Tracks')
  await page.getByLabel('API key').fill(key)
  await page.getByRole('button', { name: 'Connect', exact: true }).click()
  const rows = page.getByRole('table', { name: 'Artist track credits', exact: true }).getByRole('row')
  await expect(rows).toHaveCount(3)
  await expect(rows.nth(1).getByRole('cell').nth(4)).toHaveText('2020-01-02')
  await expect(rows.nth(1)).toContainText('Primary Artist not recorded')
  await expect(rows.nth(1).getByRole('link', { name: 'Fixture Composer', exact: true })).toHaveCount(0)
  await rows.nth(1).getByRole('button', { name: 'Track details', exact: true }).click()
  await expect(rows.nth(1)).toContainText('Credited as: Fixture Composer')
  await expect(rows.nth(2).getByRole('cell').nth(2)).toHaveText('No release placement')
  await expect(rows.nth(2).getByRole('cell').nth(3)).toContainText('Not recorded')
  await expect(rows.nth(2).getByRole('cell').nth(4)).toHaveText('Not available')
  await rows.nth(2).getByRole('button', { name: 'Track details', exact: true }).click()
  await expect(rows.nth(2)).toContainText('No release placement')
  await expect(page.getByRole('button', { name: 'Next page', exact: true })).toBeDisabled()
})

for (const tracks of [true, false]) {
  test(`Artist ${tracks ? 'track' : 'release'} appearance failures and empty results remain distinct`, async ({ page }) => {
    const path = tracks ? '/api/v1/tracks' : '/api/v1/artists/release-fixture-artist/release-appearances'
    let unavailable = true
    await page.route(`**${path}?*`, route => route.fulfill({
      status: unavailable ? 503 : 200,
      contentType: unavailable ? 'application/problem+json' : 'application/json',
      body: JSON.stringify(unavailable
        ? { detail: 'Fixture credits temporarily unavailable.', code: 'fixture_unavailable' }
        : { items: [], total: 0, page: 1, pageSize: 25 }),
    }))
    const tab = tracks ? 'Tracks' : 'Releases'
    await page.goto(`/admin/#/artists/release-fixture-artist/show?tab=${tab}`)
    await page.getByLabel('API key').fill(key)
    await page.getByRole('button', { name: 'Connect', exact: true }).click()
    await expect(page.getByRole('alert').filter({ hasText: 'Fixture credits temporarily unavailable.' })).toBeVisible()
    const empty = page.getByText(tracks ? 'No tracks recorded.' : 'No release credits recorded.', { exact: true })
    await expect(empty).toHaveCount(0)
    unavailable = false
    await page.getByRole('tab', { name: 'Featured', exact: true }).click()
    await page.getByRole('tab', { name: tab, exact: true }).click()
    await expect(empty).toBeVisible()
    await expect(page.getByRole('alert').filter({ hasText: 'Fixture credits temporarily unavailable.' })).toHaveCount(0)
    await expect(page.getByRole('button', { name: 'Previous page', exact: true })).toBeDisabled()
    await expect(page.getByRole('button', { name: 'Next page', exact: true })).toBeDisabled()
  })
}
