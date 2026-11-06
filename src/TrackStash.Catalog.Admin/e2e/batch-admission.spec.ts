import { expect, test, type Page } from '@playwright/test'

const key = 'test-only-browser-key-not-a-production-secret'

async function open(page: Page) {
  await page.goto('/admin/#/purchase-releases')
  await page.getByLabel('API key').fill(key)
  await page.getByRole('button', { name: 'Connect', exact: true }).click()
  return page.getByRole('region', { name: 'Batch admission' })
}

test('checking ready releases is a dry run that writes nothing to the catalog', async ({ page }) => {
  const posts: string[] = []
  page.on('request', request => { if (request.method() === 'POST') posts.push(request.postData() ?? '') })
  const panel = await open(page)
  await panel.getByRole('button', { name: 'Check ready releases' }).click()
  const results = panel.getByRole('table', { name: 'Batch admission results' })
  const row = results.getByRole('row', { name: /Inspector complete release/ })
  await expect(row).toBeVisible()
  await expect(row.getByText('Will admit')).toBeVisible()
  await expect(row).toContainText('26 tracks; 4 new artists; 26 new recordings')
  expect(posts.map(body => JSON.parse(body))).toEqual([{ releaseIds: [900], dryRun: true, confirmed: false }])
  await expect(panel.getByRole('button', { name: 'Check again' })).toBeEnabled()
})

test('admitting requires the backup confirmation, sends only admittable releases and reloads the candidate list', async ({ page }) => {
  let admitted: unknown
  await page.route('**/api/v1/purchase-release-admissions', async route => {
    const body = route.request().postDataJSON()
    if (body.dryRun) return route.continue()
    admitted = body
    await route.fulfill({ json: { dryRun: false, items: [{ releaseId: 900, title: 'Inspector complete release', status: 'admitted', reasons: [],
      tracks: 26, newArtists: 4, unresolvedArtists: 0, newRecordings: 26, reusedRecordings: 0, catalogReleaseId: 'release-900' }] } })
  })
  const panel = await open(page)
  await panel.getByRole('button', { name: 'Check ready releases' }).click()
  const admit = panel.getByRole('button', { name: 'Admit 1 releases' })
  await expect(admit).toBeDisabled()
  await panel.getByLabel('I have a recent catalog backup and want to admit these 1 releases').check()
  const reload = page.waitForRequest(request => request.method() === 'GET' && request.url().includes('/api/v1/purchase-release-candidates?'))
  await admit.click()
  await reload
  expect(admitted).toEqual({ releaseIds: [900], dryRun: false, confirmed: true })
  const row = panel.getByRole('table', { name: 'Batch admission results' }).getByRole('row', { name: /Inspector complete release/ })
  await expect(row.getByText('Admitted')).toBeVisible()
  await expect(panel.getByText('1 admitted.')).toBeVisible()
  await expect(admit).toBeHidden()
})
