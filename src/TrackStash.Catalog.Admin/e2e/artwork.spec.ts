import { test, expect } from '@playwright/test'
import type { Page } from '@playwright/test'

const key = 'test-only-browser-key-not-a-production-secret'
const authorization = { Authorization: `Bearer ${key}` }
async function login(page: Page, id: string) {
  await page.goto(`/admin/#/labels/${id}/show`)
  await page.getByLabel('API key').fill(key)
  await page.getByRole('button', { name: 'Connect', exact: true }).click()
  await expect(page.getByRole('button', { name: 'Manage artwork', exact: true })).toBeVisible()
}
async function png(page: Page) {
  const data = await page.evaluate(() => {
    const canvas = document.createElement('canvas')
    canvas.width = 32; canvas.height = 24
    const context = canvas.getContext('2d')!
    context.fillStyle = '#1565c0'
    context.fillRect(0, 0, 32, 24)
    return canvas.toDataURL('image/png').split(',')[1]
  })
  return Buffer.from(data, 'base64')
}

test('label artwork uploads, protected images, gallery edits and detachment survive reload', async ({ page, request }) => {
  const response = await request.post('/api/v1/labels', { headers: authorization, data: { name: 'Artwork Browser Label' } })
  const label = await response.json()
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  await login(page, label.id)
  const bytes = await png(page)
  await page.getByRole('button', { name: 'Manage artwork', exact: true }).click()
  const dialog = page.getByRole('dialog')
  await dialog.getByLabel('Artwork file', { exact: true }).setInputFiles({ name: 'logo.png', mimeType: 'image/png', buffer: bytes })
  await dialog.getByLabel('Upload caption', { exact: true }).fill('Original label logo')
  await dialog.getByLabel('Artwork source URL', { exact: true }).fill('https://example.test/label')
  await dialog.getByLabel('Artwork attribution', { exact: true }).fill('Designer')
  await dialog.getByRole('textbox', { name: 'Usage rights', exact: true }).fill('Permission recorded')
  await dialog.getByLabel('Prefer this upload').check()
  await dialog.getByRole('button', { name: 'Upload image', exact: true }).click()
  await expect(dialog.getByRole('textbox', { name: 'Caption for image 1', exact: true })).toHaveValue('Original label logo')
  await expect(dialog.getByRole('img', { name: 'Original label logo', exact: true })).toHaveAttribute('src', /^blob:/)
  await dialog.getByLabel('Artwork file', { exact: true }).setInputFiles({ name: 'gallery.png', mimeType: 'image/png', buffer: bytes })
  await dialog.getByRole('textbox', { name: 'Usage rights', exact: true }).fill('Private reference, rights unverified')
  await dialog.getByRole('button', { name: 'Upload image', exact: true }).click()
  await expect(dialog.getByRole('textbox', { name: 'Caption for image 2', exact: true })).toBeVisible()
  await dialog.getByRole('button', { name: 'Move image 2 earlier', exact: true }).click()
  await dialog.getByRole('textbox', { name: 'Caption for image 1', exact: true }).fill('Gallery first')
  await dialog.getByRole('button', { name: 'Prefer image 1', exact: true }).click()
  await dialog.getByRole('button', { name: 'Save gallery changes', exact: true }).click()
  await expect(dialog.getByRole('button', { name: 'Save gallery changes', exact: true })).toBeDisabled()
  await dialog.getByRole('button', { name: 'Close artwork', exact: true }).click()
  const stored = await (await request.get(`/api/v1/labels/${label.id}/artwork`, { headers: authorization })).json()
  expect(stored.items.map((item: { originalFilename: string }) => item.originalFilename)).toEqual(['gallery.png', 'logo.png'])
  expect(stored.items[0].isPreferred).toBe(true)
  expect(stored.items[1].isPreferred).toBe(false)
  expect((await request.get(stored.items[0].thumbnailUrl)).status()).toBe(401)
  await page.reload()
  await page.getByLabel('API key').fill(key)
  await page.getByRole('button', { name: 'Connect', exact: true }).click()
  await expect(page.getByRole('img', { name: 'Gallery first', exact: true })).toHaveAttribute('src', /^blob:/)
  await page.getByRole('button', { name: 'Manage artwork', exact: true }).click()
  await dialog.getByRole('button', { name: 'Replace image 1', exact: true }).click()
  await dialog.getByLabel('Artwork file', { exact: true }).setInputFiles({ name: 'replacement.png', mimeType: 'image/png', buffer: bytes })
  await dialog.getByRole('textbox', { name: 'Usage rights', exact: true }).fill('Replacement permission recorded')
  await dialog.getByRole('button', { name: 'Upload image', exact: true }).click()
  await expect(dialog.getByText('Image 1: replacement.png', { exact: true })).toBeVisible()
  const replaced = await (await request.get(`/api/v1/labels/${label.id}/artwork`, { headers: authorization })).json()
  expect(replaced.items[0].id).toBe(stored.items[0].id)
  expect(replaced.items[0].assetId).not.toBe(stored.items[0].assetId)
  await dialog.getByRole('button', { name: 'Remove image 1', exact: true }).click()
  await dialog.getByRole('button', { name: 'Save gallery changes', exact: true }).click()
  await expect(dialog.getByRole('button', { name: 'Save gallery changes', exact: true })).toBeDisabled()
  const detached = await (await request.get(`/api/v1/labels/${label.id}/artwork`, { headers: authorization })).json()
  expect(detached.items).toHaveLength(1)
  expect((await request.get(stored.items[0].originalUrl, { headers: authorization })).status()).toBe(200)
  const storage = await page.evaluate(() => JSON.stringify([localStorage, sessionStorage]))
  expect(storage).not.toContain(key)
  expect(errors).toEqual([])
})

test('stale artwork saves do not overwrite a competing edit; reload requires deliberate retry', async ({ page, request }) => {
  const created = await request.post('/api/v1/labels', { headers: authorization, data: { name: 'Stale Artwork Label' } })
  const label = await created.json()
  await login(page, label.id)
  const bytes = await png(page)
  const uploaded = await request.post(`/api/v1/labels/${label.id}/artwork`, {
    headers: { ...authorization, 'If-Match': created.headers().etag },
    multipart: { file: { name: 'logo.png', mimeType: 'image/png', buffer: bytes }, usageRights: 'Permission recorded' },
  })
  expect(uploaded.status()).toBe(201)
  await page.getByRole('button', { name: 'Manage artwork', exact: true }).click()
  const dialog = page.getByRole('dialog')
  await dialog.getByRole('button', { name: 'Reload artwork (discard unsaved gallery changes)', exact: true }).click()
  await expect(dialog.getByRole('textbox', { name: 'Caption for image 1', exact: true })).toBeVisible()
  await dialog.getByRole('textbox', { name: 'Caption for image 1', exact: true }).fill('Must not overwrite')
  const changed = await request.patch(`/api/v1/labels/${label.id}`, {
    headers: { ...authorization, 'If-Match': uploaded.headers().etag, 'Content-Type': 'application/merge-patch+json' },
    data: JSON.stringify({ sortName: 'Competing sort edit' }),
  })
  expect(changed.status()).toBe(200)
  await dialog.getByRole('button', { name: 'Save gallery changes', exact: true }).click()
  await expect(dialog.getByText(/Your edit was not saved/)).toBeVisible()
  const gallery = await (await request.get(`/api/v1/labels/${label.id}/artwork`, { headers: authorization })).json()
  expect(gallery.items[0].caption).toBeNull()
  await dialog.getByRole('button', { name: 'Reload artwork (discard unsaved gallery changes)', exact: true }).click()
  await expect(dialog.getByRole('textbox', { name: 'Caption for image 1', exact: true })).toHaveValue('')
})

test('merge review displays survivor-first artwork and preserves the winning preference', async ({ page, request }) => {
  const survivorResponse = await request.post('/api/v1/labels', { headers: authorization, data: { name: 'Artwork Merge Winner' } })
  const retiredResponse = await request.post('/api/v1/labels', { headers: authorization, data: { name: 'Artwork Merge Duplicate' } })
  const survivor = await survivorResponse.json()
  const retired = await retiredResponse.json()
  await login(page, survivor.id)
  const bytes = await png(page)
  for (const [label, response, caption] of [[survivor, survivorResponse, 'Winner logo'], [retired, retiredResponse, 'Other logo']] as const) {
    const uploaded = await request.post(`/api/v1/labels/${label.id}/artwork`, {
      headers: { ...authorization, 'If-Match': response.headers().etag },
      multipart: { file: { name: 'logo.png', mimeType: 'image/png', buffer: bytes }, usageRights: 'Permission recorded', caption, isPreferred: 'true' },
    })
    expect(uploaded.status()).toBe(201)
  }
  await page.getByRole('button', { name: 'Merge duplicate into this label' }).click()
  const dialog = page.getByRole('dialog')
  await dialog.getByLabel('Find duplicate label').fill('Artwork Merge Duplicate')
  await dialog.getByRole('button', { name: 'Search candidates', exact: true }).click()
  await dialog.getByRole('button', { name: `Review Artwork Merge Duplicate (${retired.id})`, exact: true }).click()
  await expect(dialog.getByText('1. Winner logo (preferred)', { exact: true })).toBeVisible()
  await expect(dialog.getByText('2. Other logo', { exact: true })).toBeVisible()
  await dialog.getByLabel('Reason for merge').fill('Confirmed duplicate label')
  await dialog.getByRole('checkbox').check()
  await dialog.getByRole('button', { name: 'Confirm merge', exact: true }).click()
  await expect(dialog).not.toBeVisible()
  const gallery = await (await request.get(`/api/v1/labels/${survivor.id}/artwork`, { headers: authorization })).json()
  expect(gallery.items.map((item: { caption: string }) => item.caption)).toEqual(['Winner logo', 'Other logo'])
  expect(gallery.items.map((item: { isPreferred: boolean }) => item.isPreferred)).toEqual([true, false])
})
