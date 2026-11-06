import { expect, test } from '@playwright/test'

const key = 'test-only-browser-key-not-a-production-secret'

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
  await expect(list.getByRole('columnheader')).toHaveText(['#', 'Title / Artist · Remixer', 'Release', 'Label / Genre · Subgenre', 'Release date', 'BPM', 'Key', 'Length', 'Releases'])
  const search = page.getByRole('textbox', { name: 'Search tracks', exact: true })
  expect((await search.boundingBox())!.width).toBeGreaterThan(300)
  await search.fill('RELTEST25')
  await expect(list.getByRole('row')).toHaveCount(2)
  await expect(page).toHaveURL(/q=RELTEST25/)
  const row = list.getByRole('row').nth(1)
  await expect(row.getByRole('cell').nth(2)).toHaveText('Fixture Catalog Release')
  await expect(row.getByRole('cell').nth(7)).toHaveText('2:03')
  await expect(row.getByRole('cell').nth(8)).toHaveText('1')
  await page.getByRole('combobox', { name: 'Sort tracks', exact: true }).click()
  await expect(page.getByRole('option')).toHaveText(['Newest release first', 'Oldest release first', 'Title A–Z', 'Artist A–Z', 'Release A–Z'])
  await page.getByRole('option', { name: 'Title A–Z', exact: true }).click()
  await expect(page).toHaveURL(/sort=title/)
  await row.getByRole('link', { name: 'Catalog Track 26', exact: true }).click()
  await expect(page.getByRole('heading', { name: 'Catalog Track 26 Original Mix', exact: true, level: 1 })).toBeVisible()
  await expect(page.getByText('ISRC: RELTEST25; length: 2:03; releases: 1', { exact: true })).toBeVisible()
  const releases = page.getByRole('table', { name: 'Recording releases', exact: true })
  await expect(releases.getByRole('row')).toHaveCount(2)
  await expect(releases.getByRole('link', { name: 'Catalog Track 26', exact: true })).toHaveCount(0)
  await releases.getByRole('button', { name: 'Track details', exact: true }).click()
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
