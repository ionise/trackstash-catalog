import { test, expect } from '@playwright/test'
import { readFile } from 'node:fs/promises'

const key = 'test-only-browser-key-not-a-production-secret'
const headers = { Authorization: `Bearer ${key}` }

test('system menu groups backup and an empty catalog shows no error', async ({ page }) => {
  await page.route('**/api/v1/backups', route => route.fulfill({
    contentType: 'application/json', body: JSON.stringify({ items: [], retainNewest: 10 }),
  }))
  await page.goto('/admin/#/labels')
  await page.getByLabel('API key').fill(key)
  await page.getByRole('button', { name: 'Connect', exact: true }).click()
  const system = page.getByRole('menuitem', { name: 'System', exact: true })
  const backup = page.getByRole('menuitem', { name: 'Backup', exact: true })
  await expect(system).toHaveAttribute('aria-expanded', 'true')
  await system.click()
  await expect(backup).toBeHidden()
  await system.click()
  await backup.click()
  await expect(page.getByRole('heading', { name: 'Catalog backups', exact: true })).toBeVisible()
  await expect(page.getByText('No saved backups for this catalog.', { exact: true })).toBeVisible()
  await expect(page.getByRole('alert').filter({ hasText: 'Request could not be completed' })).toHaveCount(0)
  await expect(backup).toHaveClass(/RaMenuItemLink-active/)
  await page.setViewportSize({ width: 390, height: 844 })
  await expect(page.getByRole('heading', { name: 'Catalog backups', exact: true })).toBeVisible()
})

test('missing backup route gives a restart instruction rather than an empty-list message', async ({ page }) => {
  await page.route('**/api/v1/backups', route => route.fulfill({
    status: 404, contentType: 'application/problem+json', body: JSON.stringify({ title: 'Request could not be completed', code: 'http_error' }),
  }))
  await page.goto('/admin/#/backups')
  await page.getByLabel('API key').fill(key)
  await page.getByRole('button', { name: 'Connect', exact: true }).click()
  await expect(page.getByRole('alert').filter({ hasText: 'Restart the API with the updated backend' })).toBeVisible()
  await expect(page.getByText('No saved backups for this catalog.', { exact: true })).toHaveCount(0)
})

test('admin creates a persistent timestamped backup and downloads the same ZIP', async ({ page, request }) => {
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  await page.goto('/admin/#/backups')
  await page.getByLabel('API key').fill(key)
  await page.getByRole('button', { name: 'Connect', exact: true }).click()
  await expect(page.getByRole('heading', { name: 'Catalog backups', exact: true })).toBeVisible()
  await page.getByRole('button', { name: 'Create backup', exact: true }).click()
  await expect(page.getByRole('alert').filter({ hasText: 'Verified and saved' })).toBeVisible({ timeout: 30_000 })
  const list = await (await request.get('/api/v1/backups', { headers })).json()
  expect(list.retainNewest).toBe(10)
  const backup = list.items[0]
  expect(backup.filename).toMatch(/^trackstash-catalog-\d{8}T\d{6}Z-[a-f0-9]{32}\.zip$/)
  // Exercise the bounded fallback in browsers without Save File support.
  await page.evaluate(() => { Object.defineProperty(window, 'showSaveFilePicker', { value: undefined, configurable: true }) })
  const event = page.waitForEvent('download')
  await page.getByRole('button', { name: 'Download ZIP', exact: true }).first().click()
  const download = await event
  expect(download.suggestedFilename()).toBe(backup.filename)
  const received = await readFile((await download.path())!)
  const stored = await request.get(`/api/v1/backups/${backup.id}/download`, { headers })
  expect(received).toEqual(await stored.body())
  expect(received.subarray(0, 2).toString()).toBe('PK')
  expect((await request.get(`/api/v1/backups/${backup.id}/download`)).status()).toBe(401)
  await page.reload()
  await page.getByLabel('API key').fill(key)
  await page.getByRole('button', { name: 'Connect', exact: true }).click()
  await expect(page.getByText(backup.filename, { exact: true })).toBeVisible()
  expect(errors).toEqual([])
})

test('admin streaming download uses authenticated bytes and displays backup job failures explicitly', async ({ page }) => {
  await page.goto('/admin/#/backups')
  await page.getByLabel('API key').fill(key)
  await page.getByRole('button', { name: 'Connect', exact: true }).click()
  await page.getByRole('button', { name: 'Create backup', exact: true }).click()
  await expect(page.getByRole('alert').filter({ hasText: 'Verified and saved' })).toBeVisible({ timeout: 30_000 })
  await page.evaluate(() => {
    Object.defineProperty(window, 'showSaveFilePicker', {
      configurable: true, value: async () => ({
        createWritable: async () => new WritableStream({
          write(chunk: Uint8Array) { document.body.dataset.downloadBytes = String(Number(document.body.dataset.downloadBytes ?? 0) + chunk.byteLength) },
          close() { document.body.dataset.downloadFinished = 'yes' },
        }),
      }),
    })
  })
  await page.getByRole('button', { name: 'Download ZIP', exact: true }).first().click()
  await expect(page.locator('body')).toHaveAttribute('data-download-finished', 'yes')
  expect(Number(await page.locator('body').getAttribute('data-download-bytes'))).toBeGreaterThan(0)
  await page.route('**/api/v1/backup-jobs', route => route.fulfill({
    status: 202, contentType: 'application/json', body: JSON.stringify({ id: 'failed-job', status: 'queued', stage: 'Queued' }),
  }))
  await page.route('**/api/v1/backup-jobs/failed-job', route => route.fulfill({
    contentType: 'application/json', body: JSON.stringify({ id: 'failed-job', status: 'failed', stage: 'Failed', error: 'Backup failed: missing original.' }),
  }))
  await page.getByRole('button', { name: 'Create backup', exact: true }).click()
  await expect(page.getByRole('alert').filter({ hasText: 'Backup failed: missing original.' })).toBeVisible()
})
