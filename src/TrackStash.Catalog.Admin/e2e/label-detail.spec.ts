import { test, expect } from '@playwright/test'
import type { Page } from '@playwright/test'

const key = 'test-only-browser-key-not-a-production-secret'
const authorization = { Authorization: `Bearer ${key}` }
async function login(page: Page, id: string) {
  await page.goto(`/admin/#/labels/${id}/show`)
  await page.getByLabel('API key').fill(key)
  await page.getByRole('button', { name: 'Connect', exact: true }).click()
  await expect(page.getByRole('button', { name: 'Favourite', exact: true })).toBeVisible()
}

test('label header, local flag, links and favourite preserve the full profile and artwork version', async ({ page, request }) => {
  const created = await request.post('/api/v1/labels', { headers: authorization, data: { name: 'Detail Header Label' } })
  const label = await created.json()
  const profile = {
    ...label.profile, description: 'Independent label.\nSecond paragraph.', countryCode: 'ES', city: 'Barcelona',
    foundedYear: 2011, lifecycleStatus: 'active', notes: 'Keep my private notes.', reviewStatus: 'reviewed',
    links: [
      { kind: 'discogs', url: 'https://www.discogs.com/label/1', title: 'Catalog', sourceUrl: 'https://example.test/source' },
      { kind: 'official', url: 'https://example.test', title: null, sourceUrl: null },
      { kind: 'other', url: 'https://unknown.example.test/label', title: 'Other site', sourceUrl: null },
    ],
  }
  const updated = await request.patch(`/api/v1/labels/${label.id}`, {
    headers: { ...authorization, 'If-Match': created.headers().etag, 'Content-Type': 'application/merge-patch+json' },
    data: JSON.stringify({ profile }),
  })
  expect(updated.status()).toBe(200)
  const savedProfile = (await updated.json()).profile
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  const externalRequests: string[] = []
  page.on('request', request => {
    if (!['localhost', '127.0.0.1'].includes(new URL(request.url()).hostname) && !request.url().startsWith('blob:'))
      externalRequests.push(request.url())
  })
  await login(page, label.id)
  await expect(page.getByRole('tab')).toHaveText(['Releases', 'Artists', 'Tracks', 'Details', 'Relationships'])
  await expect(page.getByRole('region', { name: 'Label relationships' })).toBeHidden()
  await expect(page.getByRole('region', { name: 'Label groups and families' })).toBeHidden()
  await page.getByRole('tab', { name: 'Relationships', exact: true }).click()
  const relationships = page.getByRole('tabpanel', { name: 'Relationships', exact: true })
  await expect(relationships.getByRole('region', { name: 'Label relationships' })).toBeVisible()
  await expect(relationships.getByRole('region', { name: 'Label groups and families' })).toBeVisible()
  await page.getByRole('tab', { name: 'Releases', exact: true }).click()
  await expect(page.getByRole('tab', { name: 'Releases', exact: true })).toHaveAttribute('aria-selected', 'true')
  await expect(page.getByRole('heading', { name: label.name, exact: true })).toBeVisible()
  await expect(page.getByText('Spain', { exact: true })).toBeVisible()
  const flag = page.locator('header img[alt=""]')
  await expect(flag).toHaveAttribute('src', /es.*\.svg/)
  await expect.poll(() => flag.evaluate(image => image instanceof HTMLImageElement ? image.naturalWidth : 0)).toBeGreaterThan(0)
  await expect(page.getByText('Independent label.\nSecond paragraph.', { exact: true })).toBeVisible()
  const discogs = page.getByRole('link', { name: 'Discogs: Catalog (www.discogs.com/label/1)', exact: true })
  await expect(discogs).toHaveAttribute('href', profile.links[0].url)
  await expect(discogs).toHaveAttribute('rel', 'noopener noreferrer')
  await expect(page.getByRole('link', { name: 'Edit', exact: true })).toBeVisible()
  const favourite = page.getByRole('button', { name: 'Favourite', exact: true })
  await expect(favourite).toHaveAttribute('aria-pressed', 'false')
  const galleryReload = page.waitForResponse(response => response.url().endsWith(`/labels/${label.id}/artwork`) && response.status() === 200
    && response.headers().etag !== updated.headers().etag)
  await favourite.click()
  await expect(favourite).toHaveAttribute('aria-pressed', 'true')
  await galleryReload
  const saved = await request.get(`/api/v1/labels/${label.id}`, { headers: authorization })
  expect((await saved.json()).profile).toEqual({ ...savedProfile, favourite: true })
  await page.getByRole('button', { name: 'Change label artwork', exact: true }).focus()
  await expect(page.locator('header .logo-action')).toHaveCSS('opacity', '1')
  await page.keyboard.press('Enter')
  const dialog = page.getByRole('dialog')
  const bytes = await page.evaluate(() => {
    const canvas = document.createElement('canvas')
    canvas.width = 8; canvas.height = 8
    return canvas.toDataURL('image/png').split(',')[1]
  })
  await dialog.getByLabel('Artwork file', { exact: true }).setInputFiles({ name: 'header.png', mimeType: 'image/png', buffer: Buffer.from(bytes, 'base64') })
  await dialog.getByRole('textbox', { name: 'Usage rights', exact: true }).fill('Test permission')
  await dialog.getByLabel('Prefer this upload').check()
  await dialog.getByRole('button', { name: 'Upload image', exact: true }).click()
  await expect(dialog.getByText('Image 1: header.png', { exact: true })).toBeVisible()
  await dialog.getByRole('button', { name: 'Close artwork', exact: true }).click()
  await expect(page.locator('header').getByRole('img', { name: `${label.name} logo`, exact: true })).toHaveAttribute('src', /^blob:/)
  await favourite.click()
  await expect(favourite).toHaveAttribute('aria-pressed', 'false')
  await page.getByRole('tab', { name: 'Artists', exact: true }).click()
  await expect(page.getByRole('tabpanel', { name: 'Artists', exact: true })).toContainText('No catalog Artists match this view.')
  await page.getByRole('tab', { name: 'Details', exact: true }).click()
  await expect(page.getByText(profile.notes, { exact: true })).toBeVisible()
  await page.route('**/artwork-assets/*/thumbnail', route => route.fulfill({
    status: 409, contentType: 'application/problem+json', body: JSON.stringify({ detail: 'Artwork file is unavailable.' }),
  }))
  await page.getByRole('link', { name: 'Edit', exact: true }).click()
  await expect(page.getByRole('textbox', { name: 'Name', exact: true })).toBeVisible()
  await page.goBack()
  await expect(page.locator('header').getByRole('alert')).toContainText('Artwork file is unavailable.')
  await page.getByRole('tab', { name: 'Details', exact: true }).click()
  await page.getByRole('button', { name: 'Manage artwork', exact: true }).click()
  await expect(dialog).toBeVisible()
  expect(externalRequests).toEqual([])
  expect(errors).toEqual([])
})

test('stale favourite does not overwrite competing profile edits or retry without review', async ({ page, request }) => {
  const created = await request.post('/api/v1/labels', { headers: authorization, data: { name: 'Stale Favourite Label' } })
  const label = await created.json()
  await login(page, label.id)
  const changed = await request.patch(`/api/v1/labels/${label.id}`, {
    headers: { ...authorization, 'If-Match': created.headers().etag, 'Content-Type': 'application/merge-patch+json' },
    data: JSON.stringify({ profile: { ...label.profile, notes: 'Competing notes' } }),
  })
  expect(changed.status()).toBe(200)
  let patches = 0
  page.on('request', request => { if (request.method() === 'PATCH') patches++ })
  const favourite = page.getByRole('button', { name: 'Favourite', exact: true })
  await favourite.click()
  await expect(page.getByText(/Your edit was not saved/).first()).toBeVisible()
  await expect(favourite).toBeDisabled()
  const stored = await (await request.get(`/api/v1/labels/${label.id}`, { headers: authorization })).json()
  expect(stored.profile).toMatchObject({ favourite: false, notes: 'Competing notes' })
  expect(patches).toBe(1)
  await page.getByRole('button', { name: 'Reload label', exact: true }).click()
  await page.getByRole('tab', { name: 'Details', exact: true }).click()
  await expect(page.getByText('Competing notes', { exact: true })).toBeVisible()
  await favourite.click()
  await expect(favourite).toHaveAttribute('aria-pressed', 'true')
  expect(patches).toBe(2)
})

test.describe('touch layout', () => {
  test.use({ hasTouch: true })
  test('minimal and unknown-country labels work on narrow touch screens with accessible merge and artwork actions', async ({ page, request }) => {
    await page.setViewportSize({ width: 390, height: 844 })
    await page.emulateMedia({ reducedMotion: 'reduce' })
    const created = await request.post('/api/v1/labels', {
      headers: authorization, data: { name: 'A very long label name to exercise the responsive identity header and its actions' },
    })
    const label = await created.json()
    const updated = await request.patch(`/api/v1/labels/${label.id}`, {
      headers: { ...authorization, 'If-Match': created.headers().etag, 'Content-Type': 'application/merge-patch+json' },
      data: JSON.stringify({ profile: { ...label.profile, countryCode: 'ZZ' } }),
    })
    expect(updated.status()).toBe(200)
    await login(page, label.id)
    await expect(page.getByText('No description recorded.', { exact: true })).toBeVisible()
    await expect(page.getByText('ZZ', { exact: true })).toBeVisible()
    await expect(page.locator('header img[alt=""]')).toHaveCount(0)
    await expect(page.getByText('No preferred artwork', { exact: true })).toBeVisible()
    await expect(page.getByRole('region', { name: 'Artwork gallery', exact: true })).not.toBeVisible()
    await expect(page.getByRole('region', { name: 'Personal notes', exact: true })).not.toBeVisible()
    await page.getByRole('tab', { name: 'Details', exact: true }).click()
    await expect(page.getByRole('region', { name: 'Artwork gallery', exact: true })).toBeVisible()
    await expect(page.getByRole('region', { name: 'Personal notes', exact: true })).toBeVisible()
    await page.getByRole('tab', { name: 'Tracks', exact: true }).click()
    await expect(page.locator('header .logo-action')).toHaveCSS('opacity', '1')
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true)
    await page.getByRole('button', { name: 'More actions', exact: true }).focus()
    await page.keyboard.press('Enter')
    await page.getByRole('menuitem', { name: 'Merge label...', exact: true }).click()
    await expect(page.getByRole('dialog', { name: 'Review duplicate label merge' })).toBeVisible()
    await expect(page.getByRole('button', { name: 'Confirm merge', exact: true })).toHaveCount(0)
    await page.getByRole('button', { name: 'Cancel', exact: true }).click()
    await page.getByRole('tab', { name: 'Details', exact: true }).click()
    await page.getByRole('button', { name: 'Manage artwork', exact: true }).click()
    await expect(page.getByRole('dialog', { name: 'Label artwork and gallery' })).toBeVisible()
  })
})
