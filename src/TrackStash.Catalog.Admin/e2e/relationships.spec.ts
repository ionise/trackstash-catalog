import { test, expect } from '@playwright/test'
import type { APIRequestContext, Page } from '@playwright/test'

const key = 'test-only-browser-key-not-a-production-secret'
const headers = { Authorization: `Bearer ${key}` }
async function create(request: APIRequestContext, name: string) {
  const response = await request.post('/api/v1/labels', { headers, data: { name } })
  expect(response.status()).toBe(201)
  return response.json()
}
async function signIn(page: Page, id: string) {
  await page.goto(`/admin/#/labels/${id}/show`)
  await page.getByLabel('API key').fill(key)
  await page.getByRole('button', { name: 'Connect', exact: true }).click()
  await page.getByRole('button', { name: 'Manage relationships', exact: true }).click()
}

test('relationships support reviewed editing, inverse navigation, cancellation and removal', async ({ page, request }) => {
  const child = await create(request, 'Relationship child')
  const parent = await create(request, 'Relationship parent')
  await signIn(page, child.id)
  const dialog = page.getByRole('dialog', { name: 'Manage label relationships' })
  await dialog.getByLabel('Find related label').fill('Relationship parent')
  await dialog.getByRole('button', { name: 'Search labels', exact: true }).click()
  await dialog.getByRole('button', { name: `Add Relationship parent (${parent.id})`, exact: true }).click()
  await dialog.getByRole('textbox', { name: /^Source 1/ }).fill('Official parent site')
  await dialog.getByLabel('Evidence URL 1', { exact: true }).fill('https://example.test/parent')
  await dialog.getByLabel('Relationship notes 1', { exact: true }).fill('Reviewed imprint connection')
  await expect(dialog.getByRole('button', { name: 'Save relationships', exact: true })).toBeDisabled()
  await dialog.getByRole('checkbox', { name: 'I have reviewed these relationships and removals.' }).check()
  await dialog.getByRole('button', { name: 'Save relationships', exact: true }).click()
  await expect(dialog).toBeHidden()
  const section = page.getByRole('region', { name: 'Label relationships' })
  await expect(section.getByText('Imprint of:', { exact: false })).toBeVisible()
  await expect(section.getByRole('link', { name: 'Evidence', exact: true })).toHaveAttribute('href', 'https://example.test/parent')
  await section.getByRole('link', { name: 'Relationship parent', exact: true }).click()
  await expect(page.getByRole('heading', { name: 'Relationship parent', exact: true })).toBeVisible()
  await expect(section.getByRole('link', { name: 'Relationship child', exact: true })).toBeVisible()
  await section.getByRole('link', { name: 'Relationship child', exact: true }).click()
  await page.getByRole('button', { name: 'Manage relationships', exact: true }).click()
  await dialog.getByRole('button', { name: 'Remove relationship 1', exact: true }).click()
  await dialog.getByRole('button', { name: 'Cancel', exact: true }).click()
  await expect(section.getByRole('link', { name: 'Relationship parent', exact: true })).toBeVisible()
  await page.getByRole('button', { name: 'Manage relationships', exact: true }).click()
  await expect(dialog.getByRole('textbox', { name: /^Source 1/ })).toHaveValue('Official parent site')
  await dialog.getByRole('combobox', { name: 'Relationship 1', exact: true }).click()
  await page.getByRole('option', { name: 'Distributed by', exact: true }).click()
  await dialog.getByRole('checkbox', { name: 'I have reviewed these relationships and removals.' }).check()
  await dialog.getByRole('button', { name: 'Save relationships', exact: true }).click()
  await expect(section.getByText('Distributed by:', { exact: false })).toBeVisible()
  await page.getByRole('button', { name: 'Manage relationships', exact: true }).click()
  await dialog.getByRole('button', { name: 'Remove relationship 1', exact: true }).click()
  await dialog.getByRole('checkbox', { name: 'I have reviewed these relationships and removals.' }).check()
  await dialog.getByRole('button', { name: 'Save relationships', exact: true }).click()
  await expect(section.getByText('No label relationships recorded.', { exact: true })).toBeVisible()
})

test('stale target changes require reload and deliberate confirmation, without retry', async ({ page, request }) => {
  const child = await create(request, 'Stale relationship child')
  const parent = await create(request, 'Stale relationship parent')
  await signIn(page, child.id)
  const dialog = page.getByRole('dialog', { name: 'Manage label relationships' })
  await dialog.getByLabel('Find related label').fill('Stale relationship parent')
  await dialog.getByRole('button', { name: 'Search labels', exact: true }).click()
  await dialog.getByRole('button', { name: `Add Stale relationship parent (${parent.id})`, exact: true }).click()
  const original = await request.get(`/api/v1/labels/${parent.id}`, { headers })
  expect((await request.patch(`/api/v1/labels/${parent.id}`, {
    headers: { ...headers, 'Content-Type': 'application/merge-patch+json', 'If-Match': original.headers().etag },
    data: { name: 'Changed related label' },
  })).status()).toBe(200)
  let writes = 0
  page.on('request', req => { if (req.method() === 'PATCH' && req.url().endsWith('/relationships')) writes++ })
  await dialog.getByRole('checkbox', { name: 'I have reviewed these relationships and removals.' }).check()
  await dialog.getByRole('button', { name: 'Save relationships', exact: true }).click()
  await expect(dialog.getByRole('alert')).toContainText('No relationships were saved')
  await expect(dialog.getByRole('button', { name: 'Save relationships', exact: true })).toBeDisabled()
  expect(writes).toBe(1)
  await dialog.getByRole('button', { name: 'Reload relationships', exact: true }).click()
  await expect(dialog.getByRole('checkbox', { name: 'I have reviewed these relationships and removals.' })).not.toBeChecked()
  await expect(dialog.getByRole('textbox', { name: /^Source 1/ })).toHaveCount(0)
  await expect(dialog.getByRole('button', { name: 'Save relationships', exact: true })).toBeDisabled()
})

test('merge review displays relationship transfer before confirmation', async ({ page, request }) => {
  const survivor = await create(request, 'Relationship merge survivor')
  const retired = await create(request, 'Relationship merge duplicate')
  const parent = await create(request, 'Relationship merge parent')
  const graph = await request.get(`/api/v1/labels/${retired.id}/relationships`, { headers })
  expect((await request.patch(`/api/v1/labels/${retired.id}/relationships`, {
    headers: { ...headers, 'If-Match': graph.headers().etag },
    data: { items: [{ id: null, toLabelId: parent.id, type: 'imprint_of', source: 'manual', sourceUrl: null, notes: 'Keep evidence', targetVersion: parent.version }] },
  })).status()).toBe(200)
  await page.goto(`/admin/#/labels/${survivor.id}/show`)
  await page.getByLabel('API key').fill(key)
  await page.getByRole('button', { name: 'Connect', exact: true }).click()
  await page.getByRole('button', { name: 'More actions', exact: true }).click()
  await page.getByRole('menuitem', { name: 'Merge label...', exact: true }).click()
  const dialog = page.getByRole('dialog', { name: 'Review duplicate label merge' })
  await dialog.getByLabel('Find duplicate label').fill('Relationship merge duplicate')
  await dialog.getByRole('button', { name: 'Search candidates', exact: true }).click()
  await dialog.getByRole('button', { name: `Review Relationship merge duplicate (${retired.id})`, exact: true }).click()
  await expect(dialog.getByRole('heading', { name: 'Resulting label relationships (1)', exact: true })).toBeVisible()
  await expect(dialog.getByText('Source: manual; evidence: (none); notes: Keep evidence', { exact: true })).toBeVisible()
  await dialog.getByRole('button', { name: 'Cancel', exact: true }).click()
  expect((await request.get(`/api/v1/labels/${retired.id}`, { headers })).status()).toBe(200)
})

test.describe('narrow touch relationship review', () => {
  test.use({ viewport: { width: 390, height: 844 }, hasTouch: true })
  test('rejects a hierarchy cycle visibly and preserves the related-content tabs', async ({ page, request }) => {
    const child = await create(request, `Cycle child ${'x'.repeat(120)}`)
    const parent = await create(request, 'Cycle parent')
    const graph = await request.get(`/api/v1/labels/${child.id}/relationships`, { headers })
    expect((await request.patch(`/api/v1/labels/${child.id}/relationships`, {
      headers: { ...headers, 'If-Match': graph.headers().etag },
      data: { items: [{ id: null, toLabelId: parent.id, type: 'imprint_of', source: 'manual', sourceUrl: null, notes: null, targetVersion: parent.version }] },
    })).status()).toBe(200)
    await signIn(page, parent.id)
    const dialog = page.getByRole('dialog', { name: 'Manage label relationships' })
    await dialog.getByLabel('Find related label').fill('Cycle child')
    await dialog.getByRole('button', { name: 'Search labels', exact: true }).click()
    await dialog.getByRole('button', { name: `Add ${child.name} (${child.id})`, exact: true }).click()
    await dialog.getByRole('checkbox', { name: 'I have reviewed these relationships and removals.' }).check()
    await dialog.getByRole('button', { name: 'Save relationships', exact: true }).click()
    await expect(dialog.getByRole('alert')).toContainText('cycle')
    expect(await dialog.evaluate(element => element.scrollWidth <= element.clientWidth)).toBe(true)
    await dialog.getByRole('button', { name: 'Cancel', exact: true }).click()
    await expect(page.getByRole('tab')).toHaveText(['Releases', 'Artists', 'Tracks', 'Details'])
    const response = await request.get(`/api/v1/labels/${parent.id}/relationships`, { headers })
    expect((await response.json()).items).toHaveLength(1) // The existing incoming edge was not replaced.
  })
})
