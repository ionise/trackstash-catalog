import { test, expect } from '@playwright/test'

const key = 'test-only-browser-key-not-a-production-secret'
const authorization = { Authorization: `Bearer ${key}` }

test('sign in, inspect, create, edit, clear sort name, and sign out on reload', async ({ page }) => {
  const browserErrors: string[] = []
  page.on('pageerror', error => browserErrors.push(error.message))
  await page.goto('/admin/')
  await page.getByLabel('API key').fill('incorrect')
  await page.getByRole('button', { name: 'Connect', exact: true }).click()
  await expect(page.getByRole('alert')).toBeVisible()
  await page.getByLabel('API key').fill(key)
  await page.getByRole('button', { name: 'Connect', exact: true }).click()
  await expect(page.getByRole('cell', { name: 'Archive Label', exact: true })).toBeVisible()
  await page.getByRole('cell', { name: 'Archive Label', exact: true }).click()
  await expect(page.getByText('Archive Alias', { exact: true })).toBeVisible()
  await expect(page.getByRole('cell', { name: 'beatport', exact: true })).toBeVisible()
  await page.getByRole('menuitem').filter({ hasText: 'Labels' }).click()
  await page.getByRole('link', { name: 'Create', exact: true }).click()
  await page.getByRole('textbox', { name: 'Name', exact: true }).fill('Browser Test Records')
  await page.getByRole('textbox', { name: 'Sort name', exact: true }).fill('Browser Sort')
  await page.getByRole('button', { name: 'Save', exact: true }).click()
  await expect(page.getByText('Browser Test Records', { exact: true }).first()).toBeVisible()
  await page.getByRole('link', { name: 'Edit', exact: true }).click()
  await page.getByRole('textbox', { name: 'Name', exact: true }).fill('Browser Edited Records')
  await page.getByRole('textbox', { name: 'Sort name', exact: true }).clear()
  await page.getByRole('button', { name: 'Save', exact: true }).click()
  await expect(page.getByText('Browser Edited Records', { exact: true }).first()).toBeVisible()
  await expect(page).toHaveURL(/\/show$/)
  const storage = await page.evaluate(() => ({
    local: Object.values(localStorage), session: Object.values(sessionStorage),
  }))
  expect(JSON.stringify(storage)).not.toContain(key)
  const refreshed = await page.reload()
  expect(refreshed?.status()).toBe(200)
  await expect(page.getByLabel('API key')).toBeVisible().catch(async error => {
    throw new Error(`${String(error)}; URL: ${page.url()}; body: ${await page.locator('body').innerText()}; errors: ${browserErrors.join(';')}`)
  })
  expect(browserErrors).toEqual([])
})

test('stale edits are rejected without overwriting a competing edit', async ({ page, request }) => {
  const created = await request.post('/api/v1/labels', { headers: authorization, data: { name: 'Stale Browser Label' } })
  const label = await created.json()
  await page.goto(`/admin/#/labels/${label.id}`)
  await page.getByLabel('API key').fill(key)
  await page.getByRole('button', { name: 'Connect', exact: true }).click()
  await expect(page.getByRole('textbox', { name: 'Name', exact: true })).toHaveValue('Stale Browser Label')
  const competing = await request.patch(`/api/v1/labels/${label.id}`, {
    headers: { ...authorization, 'If-Match': created.headers().etag, 'Content-Type': 'application/merge-patch+json' },
    data: JSON.stringify({ name: 'Competing Edit' }),
  })
  expect(competing.status()).toBe(200)
  await page.getByRole('textbox', { name: 'Name', exact: true }).fill('Must Not Overwrite')
  await page.getByRole('button', { name: 'Save', exact: true }).click()
  await expect(page.getByText(/Your edit was not saved/)).toBeVisible()
  const stored = await request.get(`/api/v1/labels/${label.id}`, { headers: authorization })
  expect((await stored.json()).name).toBe('Competing Edit')
})

test('list search, pagination, sorting, and explicit logout work', async ({ page, request }) => {
  const browserErrors: string[] = []
  page.on('pageerror', error => browserErrors.push(error.message))
  for (let index = 0; index < 27; index++) {
    const response = await request.post('/api/v1/labels', {
      headers: authorization, data: { name: `Paging Label ${String(index).padStart(2, '0')}` },
    })
    expect(response.status()).toBe(201)
  }
  await page.goto('/admin/')
  await page.getByLabel('API key').fill(key)
  await page.getByRole('button', { name: 'Connect', exact: true }).click()
  await page.getByRole('textbox', { name: 'Search names and aliases' }).fill('Paging Label')
  await expect(page.getByRole('cell', { name: 'Paging Label 00', exact: true })).toBeVisible()
  await expect(page.getByRole('cell', { name: 'Paging Label 26', exact: true })).not.toBeVisible()
  await page.getByRole('button', { name: 'Go to next page' }).click()
  await expect(page.getByRole('cell', { name: 'Paging Label 26', exact: true })).toBeVisible()
  await page.getByRole('button', { name: 'Go to previous page' }).click()
  await page.getByRole('button', { name: 'Sort by name descending', exact: true }).click()
  await expect(page.getByRole('cell', { name: 'Paging Label 26', exact: true })).toBeVisible()
  await page.getByRole('button', { name: 'Profile' }).click()
  await page.getByText('Logout', { exact: true }).click()
  await expect(page.getByLabel('API key')).toBeVisible()
  await page.goBack()
  await expect(page.getByLabel('API key')).toBeVisible()
  await expect(page.getByRole('cell', { name: 'Paging Label 26', exact: true })).not.toBeVisible()
  expect(browserErrors).toEqual([])
})
