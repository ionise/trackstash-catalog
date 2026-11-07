import { expect, test } from '@playwright/test'
import type { Page } from '@playwright/test'

const key = 'test-only-browser-key-not-a-production-secret'

const label = { id: 42, name: 'Fixture Beatport Label' }
const release = (id: number, fields: Record<string, unknown> = {}) => ({
  id, name: `Beatport release ${id}`, type: 'EP', artists: [{ id: 1, name: 'Fixture Artist' }], remixers: [], label,
  catalogLabelIds: ['legacy-test'], catalogNumber: `BP${id}`, publishDate: '2024-05-01', trackCount: 2, imageUrl: null,
  beatportUrl: `https://www.beatport.com/release/r/${id}`, catalogReleaseIds: [], inPurchaseHistory: false, purchaseSourceComplete: null,
  ...fields,
})

async function connect(page: Page, path: string) {
  await page.goto(`/admin/#${path}`)
  await page.getByLabel('API key').fill(key)
  await page.getByRole('button', { name: 'Connect', exact: true }).click()
}

test('browse Beatport releases by name and label with ownership status, tracks and paging without catalog writes', async ({ page }) => {
  const requests: string[] = []
  const writes: string[] = []
  page.on('request', request => { if (request.url().includes('/api/v1/') && request.method() !== 'GET') writes.push(request.url()) })
  await page.route('**/api/v1/beatport/releases?*', route => {
    const url = new URL(route.request().url())
    requests.push(url.search)
    const pageNumber = Number(url.searchParams.get('page'))
    const items = url.searchParams.get('labelId')
      ? [release(30)]
      : pageNumber === 2 ? [release(20)] : [
        release(10, { catalogReleaseIds: ['release-fixture'], inPurchaseHistory: true, purchaseSourceComplete: true }),
        release(11, { inPurchaseHistory: true, purchaseSourceComplete: false, catalogLabelIds: [] }),
        release(12),
      ]
    return route.fulfill({ json: { items, total: url.searchParams.get('labelId') ? 1 : 30, page: pageNumber, pageSize: 25 } })
  })
  await page.route('**/api/v1/beatport/releases/10', route => route.fulfill({ json: {
    release: release(10), trackTotal: 2, tracks: [
      { id: 101, number: 1, name: 'First cut', mixName: 'Original Mix', artists: [{ id: 1, name: 'Fixture Artist' }], remixers: [],
        isrc: 'GBTEST0001', lengthMs: 123456, bpm: 124, key: 'A Minor', genre: 'House', beatportUrl: 'https://www.beatport.com/track/t/101' },
      { id: 102, number: 2, name: 'Second cut', mixName: 'Dub', artists: [], remixers: [{ id: 2, name: 'Remixer' }],
        isrc: null, lengthMs: null, bpm: null, key: null, genre: null, beatportUrl: 'https://www.beatport.com/track/t/102' },
    ] } }))
  await page.route('**/api/v1/beatport/labels/42', route => route.fulfill({ json: {
    ...label, imageUrl: null, beatportUrl: 'https://www.beatport.com/label/l/42', catalogLabelIds: ['legacy-test'] } }))

  await connect(page, '/beatport')
  await expect(page.getByRole('heading', { name: 'Beatport', level: 1 })).toBeVisible()
  await page.getByLabel('Release name').fill('fixture & co')
  await page.getByRole('button', { name: 'Search', exact: true }).click()
  const results = page.getByRole('table', { name: 'Beatport releases' })
  await expect(results.getByText('Beatport release 10')).toBeVisible()
  expect(requests[0]).toBe('?q=fixture+%26+co&sort=newest&page=1&pageSize=25')
  const owned = results.getByRole('row').filter({ hasText: 'Beatport release 10' })
  await expect(owned.getByRole('link', { name: 'In catalog' })).toHaveAttribute('href', '#/releases/release-fixture/show')
  await expect(owned.getByText('Purchased')).toBeVisible()
  await expect(results.getByRole('row').filter({ hasText: 'Beatport release 12' }).getByText('Not owned')).toBeVisible()
  await expect(owned.getByRole('link', { name: 'Open Beatport release 10 on Beatport' })).toHaveAttribute('href', 'https://www.beatport.com/release/r/10')

  await owned.getByRole('button', { name: 'Show tracks for Beatport release 10' }).click()
  const tracks = page.getByRole('table', { name: 'Tracks on Beatport release 10' })
  await expect(tracks.getByRole('row').filter({ hasText: 'First cut (Original Mix)' })).toContainText('2:03')
  await expect(tracks.getByRole('row').filter({ hasText: 'First cut' })).toContainText('A Minor')
  await expect(tracks.getByText('Second cut (Dub)')).toBeVisible()

  await page.getByRole('combobox', { name: 'Order' }).click()
  await page.getByRole('option', { name: 'Oldest first' }).click()
  await expect.poll(() => requests.at(-1)).toBe('?q=fixture+%26+co&sort=oldest&page=1&pageSize=25')
  await page.getByRole('button', { name: 'Next page' }).click()
  await expect(results.getByText('Beatport release 20')).toBeVisible()
  await expect(page.getByText('Page 2 of 2')).toBeVisible()

  await results.getByRole('button', { name: 'Browse Fixture Beatport Label releases' }).click()
  await expect(results.getByText('Beatport release 30')).toBeVisible()
  expect(requests.at(-1)).toBe('?labelId=42&sort=oldest&page=1&pageSize=25')
  const filter = page.getByLabel('Label filter')
  await expect(filter.getByText('Fixture Beatport Label')).toBeVisible()
  await expect(filter.getByRole('link', { name: 'Catalog label' })).toHaveAttribute('href', '#/labels/legacy-test/show')
  await filter.getByRole('button', { name: 'Clear label' }).click()
  await expect(page.getByText('Search for a release by name, or pick a label to see its releases.')).toBeVisible()
  expect(writes).toEqual([])
})

test('label search, sign-in and broker errors are explained, and catalog labels open their embedded Beatport tab', async ({ page }) => {
  let signedIn = false
  await page.route('**/api/v1/beatport/labels?*', route => signedIn
    ? route.fulfill({ json: { items: [{ ...label, imageUrl: null, beatportUrl: 'https://www.beatport.com/label/l/42', catalogLabelIds: [] }], total: 1, page: 1, pageSize: 25 } })
    : route.fulfill({ status: 409, json: { detail: 'Sign in to Beatport first.', code: 'beatport_sign_in_required' } }))
  await page.route('**/api/v1/beatport/releases?*', route => route.fulfill({ status: 503,
    json: { detail: 'The Beatport broker is not configured.', code: 'beatport_broker_not_configured' } }))
  await page.route('**/api/v1/beatport/labels/42', route => route.fulfill({ json: {
    ...label, imageUrl: null, beatportUrl: 'https://www.beatport.com/label/l/42', catalogLabelIds: ['legacy-test'] } }))

  await connect(page, '/beatport?mode=labels')
  await page.getByLabel('Label name').fill('fixture')
  await page.getByRole('button', { name: 'Search', exact: true }).click()
  const error = page.getByRole('alert').filter({ hasText: 'Sign in to Beatport first' })
  await expect(error.getByRole('link', { name: 'Purchase releases' })).toHaveAttribute('href', '#/purchase-releases')
  await expect(page).toHaveURL(/#\/beatport/)

  signedIn = true
  await page.getByRole('button', { name: 'Search', exact: true }).click()
  await page.getByLabel('Label name').fill('fixture label')
  await page.getByRole('button', { name: 'Search', exact: true }).click()
  const labels = page.getByRole('table', { name: 'Beatport labels' })
  await labels.getByRole('button', { name: 'Browse releases' }).click()
  await expect(page.getByText('Browsing Beatport needs the broker that start-prod.sh starts.')).toBeVisible()

  await page.goto('/admin/#/labels/legacy-test/show')
  await page.getByRole('button', { name: 'Browse on Beatport' }).click()
  await expect(page).toHaveURL(/#\/labels\/legacy-test\/show\?tab=Beatport$/)
  await expect(page.getByRole('tabpanel', { name: 'Beatport' })).toContainText('Beatport label ID: 42')
  await expect(page.getByRole('tabpanel', { name: 'Beatport' }).getByRole('alert').filter({ hasText: 'start-prod.sh' })).toBeVisible()
})

test('Label Beatport tab loads lazily, keeps browsing inside the label and preserves its filters on reload', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 })
  const requests: URL[] = []
  const writes: string[] = []
  page.on('request', request => { if (request.url().includes('/api/v1/') && request.method() !== 'GET') writes.push(request.url()) })
  await page.route('**/api/v1/beatport/releases?*', route => {
    const url = new URL(route.request().url())
    requests.push(url)
    const number = Number(url.searchParams.get('page'))
    return route.fulfill({ json: { items: [release(number === 2 ? 20 : 10)], total: 30, page: number, pageSize: 25 } })
  })
  await page.route('**/api/v1/beatport/releases/10', route => route.fulfill({ json: {
    release: release(10), trackTotal: 0, tracks: [],
  } }))
  await connect(page, '/labels/legacy-test/show')
  await expect(page.getByRole('heading', { name: 'Archive Label', level: 1 })).toBeVisible()
  expect(requests).toHaveLength(0)
  await page.getByRole('button', { name: 'Browse on Beatport' }).click()
  const tab = page.getByRole('tabpanel', { name: 'Beatport', exact: true })
  await expect(tab.getByText('Beatport release 10', { exact: true })).toBeVisible()
  expect(requests[0].search).toBe('?labelId=42&sort=newest&page=1&pageSize=25')
  await tab.getByRole('button', { name: 'Show tracks for Beatport release 10' }).click()
  await expect(tab.getByRole('table', { name: 'Tracks on Beatport release 10' })).toBeVisible()
  await tab.getByLabel('Release name').fill('new & old')
  await tab.getByRole('button', { name: 'Search', exact: true }).click()
  await expect.poll(() => requests.at(-1)?.searchParams.get('q')).toBe('new & old')
  await tab.getByRole('combobox', { name: 'Order' }).click()
  await page.getByRole('option', { name: 'Oldest first' }).click()
  await expect.poll(() => requests.at(-1)?.searchParams.get('sort')).toBe('oldest')
  await tab.getByRole('button', { name: 'Next page' }).click()
  await expect(tab.getByText('Beatport release 20', { exact: true })).toBeVisible()
  await expect(page).toHaveURL(/#\/labels\/legacy-test\/show\?tab=Beatport&bpQ=new\+%26\+old&bpSort=oldest&bpPage=2$/)
  await page.reload()
  await page.getByLabel('API key').fill(key)
  await page.getByRole('button', { name: 'Connect', exact: true }).click()
  await expect(tab.getByText('Beatport release 20', { exact: true })).toBeVisible()
  await expect(tab.getByLabel('Release name')).toHaveValue('new & old')
  await page.getByRole('tab', { name: 'Details', exact: true }).click()
  await page.getByRole('tab', { name: 'Beatport', exact: true }).click()
  await expect(tab.getByText('Page 2 of 2')).toBeVisible()
  expect(requests.every(url => url.searchParams.get('labelId') === '42' && !url.searchParams.has('artistId'))).toBe(true)
  const width = await page.evaluate(() => ({ content: document.documentElement.scrollWidth, viewport: innerWidth }))
  expect(width.content).toBeLessThanOrEqual(width.viewport + 1)
  expect(writes).toEqual([])
})

test('Artist Beatport tab prefers the primary ID and lets you browse another identity without changing catalog tab state', async ({ page }) => {
  const requests: URL[] = []
  await page.route('**/api/v1/artists/release-fixture-artist', async route => {
    const response = await route.fetch()
    const artist = await response.json()
    await route.fulfill({ response, json: { ...artist, externalReferences: [
      { source: 'beatport', externalId: '2002', isPrimary: false },
      { source: 'beatport', externalId: '1001', isPrimary: true },
    ] } })
  })
  await page.route('**/api/v1/beatport/releases?*', route => {
    const url = new URL(route.request().url())
    requests.push(url)
    const number = Number(url.searchParams.get('page'))
    return route.fulfill({ json: { items: [release(Number(url.searchParams.get('artistId')))], total: 30, page: number, pageSize: 25 } })
  })
  await connect(page, '/artists/release-fixture-artist/show?tab=Beatport&releasePage=2')
  const tab = page.getByRole('tabpanel', { name: 'Beatport', exact: true })
  await expect(tab.getByText('Beatport release 1001', { exact: true })).toBeVisible()
  expect(requests[0].search).toBe('?artistId=1001&sort=newest&page=1&pageSize=25')
  await expect(page.getByRole('heading', { name: 'Fixture Composer', level: 1 })).toBeVisible()
  await tab.getByRole('button', { name: 'Next page' }).click()
  await expect(tab.getByText('Page 2 of 2')).toBeVisible()
  await tab.getByRole('combobox', { name: 'Beatport identity' }).click()
  await page.getByRole('option', { name: '2002', exact: true }).click()
  await expect(tab.getByText('Beatport release 2002', { exact: true })).toBeVisible()
  expect(requests.at(-1)?.search).toBe('?artistId=2002&sort=newest&page=1&pageSize=25')
  await expect(page).toHaveURL(/#\/artists\/release-fixture-artist\/show\?tab=Beatport&releasePage=2&bpIdentity=2002$/)
  await page.reload()
  await page.getByLabel('API key').fill(key)
  await page.getByRole('button', { name: 'Connect', exact: true }).click()
  await expect(tab.getByText('Beatport release 2002', { exact: true })).toBeVisible()
  expect(requests.every(url => !url.searchParams.has('labelId'))).toBe(true)
})

test('entity Beatport tabs do not guess identity or call the broker when no Beatport reference is recorded', async ({ page }) => {
  const requests: string[] = []
  page.on('request', request => { if (request.url().includes('/api/v1/beatport/')) requests.push(request.url()) })
  await connect(page, '/labels/release-other-label/show?tab=Beatport')
  await expect(page.getByRole('tabpanel', { name: 'Beatport' })).toContainText('This label has no usable Beatport ID recorded.')
  await page.goto('/admin/#/artists/release-fixture-remixer/show?tab=Beatport')
  await expect(page.getByRole('tabpanel', { name: 'Beatport' })).toContainText('This artist has no usable Beatport ID recorded.')
  expect(requests).toEqual([])
})
