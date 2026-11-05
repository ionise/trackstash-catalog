import { expect, test } from '@playwright/test'

const key = 'test-only-browser-key-not-a-production-secret'

test('inspect complete and incomplete cached releases, structured credits, label navigation and track paging without writes', async ({ page }) => {
  const writes: string[] = []
  page.on('request', request => {
    if (request.url().includes('/api/v1/') && request.method() !== 'GET') writes.push(request.method())
  })
  await page.goto('/admin/#/purchase-releases')
  await page.getByLabel('API key').fill(key)
  await page.getByRole('button', { name: 'Connect', exact: true }).click()
  await expect(page.getByRole('heading', { name: 'Purchase release inspector', level: 1 })).toBeVisible()
  await expect(page.getByText('2 corpus-flagged release candidates')).toBeVisible()
  await page.getByRole('button', { name: 'Inspect Inspector complete release (900)' }).click()
  const inspector = page.getByRole('region', { name: 'Release inspection' })
  await expect(inspector.getByRole('heading', { name: 'Inspector complete release (900)' })).toBeVisible()
  await expect(inspector.getByText(/Cached source coverage complete/)).toBeVisible()
  const tracks = inspector.getByRole('table', { name: 'Source release tracks' })
  await expect(tracks.getByRole('row')).toHaveCount(26)
  const first = tracks.getByRole('row').nth(1)
  await expect(first).toContainText('Kasey Taylor (4907)')
  await expect(first).toContainText('Amega (635680)')
  await expect(first).toContainText('GMJ (69845)')
  await expect(first).toContainText('Matter (118649)')
  await expect(first).toContainText('remixer')
  await inspector.getByRole('button', { name: 'Next tracks' }).click()
  await expect(tracks.getByRole('row')).toHaveCount(2)
  await expect(tracks).toContainText('Inspector track 26')
  await expect(inspector.getByRole('button', { name: 'Next tracks' })).toBeDisabled()
  await page.getByRole('button', { name: 'Inspect Inspector missing release (901)' }).click()
  await expect(inspector.getByText(/Cached source incomplete or contradictory/)).toBeVisible()
  await expect(inspector).toContainText('release_payload_invalid')
  await expect(page.getByRole('button', { name: /^Apply reviewed/ })).toHaveCount(0)
  await page.getByRole('textbox', { name: 'Release title or Beatport ID' }).fill('900')
  await page.getByRole('button', { name: 'Search candidates' }).click()
  await expect(page.getByText('1 corpus-flagged release candidates')).toBeVisible()
  await page.getByRole('button', { name: 'Inspect Inspector complete release (900)' }).click()
  await inspector.getByRole('link', { name: 'Archive Label', exact: true }).click()
  await expect(page.getByRole('heading', { name: 'Archive Label', exact: true })).toBeVisible()
  expect(writes).toEqual([])
})

test('source failures and delayed responses are visible without showing a previous release as the selected one', async ({ page }) => {
  await page.goto('/admin/#/purchase-releases')
  await page.getByLabel('API key').fill(key)
  await page.getByRole('button', { name: 'Connect', exact: true }).click()
  await page.getByRole('button', { name: 'Inspect Inspector complete release (900)' }).click()
  await expect(page.getByRole('heading', { name: 'Inspector complete release (900)' })).toBeVisible()
  let releaseResponse: (() => void) | undefined
  const delayed = new Promise<void>(resolve => { releaseResponse = resolve })
  await page.route('**/api/v1/purchase-release-candidates/901?*', async route => {
    await delayed
    await route.fulfill({ status: 503, contentType: 'application/problem+json', body: JSON.stringify({
      title: 'Purchase source unavailable', detail: 'Test source is unavailable.', code: 'purchase_source_unavailable',
    }) })
  })
  await page.getByRole('button', { name: 'Inspect Inspector missing release (901)' }).click()
  await expect(page.getByRole('progressbar', { name: 'Loading release inspection' })).toBeVisible()
  await expect(page.getByRole('heading', { name: 'Inspector complete release (900)' })).toHaveCount(0)
  releaseResponse?.()
  await expect(page.getByText('Test source is unavailable.', { exact: true })).toBeVisible()
})
