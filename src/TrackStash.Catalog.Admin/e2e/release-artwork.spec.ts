import { expect, test } from '@playwright/test'

const key = 'test-only-browser-key-not-a-production-secret'

test('Beatport artwork preview is explicit and reviewed attachment preserves preference by default', async ({ page }) => {
  let previews = 0
  let attached = false
  await page.route('**/api/v1/releases/*/artwork-candidates', route => route.fulfill({ json: {
    providers: ['beatport'],
    items: [{ provider: 'beatport', candidateId: 'release-fixture:10', revision: 'r1', title: 'Beatport cover',
      sourceUrl: 'https://www.beatport.com/release/-/10', attribution: 'Supplied by Beatport' }],
  } }))
  const bytes = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aX1cAAAAASUVORK5CYII=', 'base64')
  await page.route('**/api/v1/releases/*/artwork-candidates/preview?*', route => {
    previews++
    return route.fulfill({ contentType: 'image/png', body: bytes })
  })
  await page.route('**/api/v1/releases/*/artwork-candidates/attach', async route => {
    const body = route.request().postDataJSON()
    expect(body).toMatchObject({ provider: 'beatport', candidateId: 'release-fixture:10', confirmed: true, isPreferred: false })
    expect(route.request().headers()['if-match']).toBeTruthy()
    attached = true
    await route.fulfill({ status: 201, headers: { ETag: '"r2"' }, json: { releaseId: 'release-fixture', items: [] } })
  })
  await page.goto('/admin/#/releases/release-fixture/show?tab=Details')
  await page.getByLabel('API key').fill(key)
  await page.getByRole('button', { name: 'Connect', exact: true }).click()
  await page.getByRole('button', { name: 'Change release artwork', exact: true }).click()
  const dialog = page.getByRole('dialog')
  await dialog.getByRole('button', { name: 'Look up artwork candidates' }).click()
  await expect(dialog.getByText('Beatport cover (beatport)', { exact: true })).toBeVisible()
  expect(previews).toBe(0)
  await dialog.getByRole('button', { name: 'Preview artwork', exact: true }).click()
  await expect(dialog.getByRole('img', { name: 'beatport artwork candidate' })).toHaveAttribute('src', /^blob:/)
  expect(previews).toBe(1)
  const attach = dialog.getByRole('button', { name: /Attach candidate/ })
  await expect(attach).toBeDisabled()
  await dialog.getByLabel('Candidate usage rights').fill('Reviewed usage permission')
  await dialog.getByLabel('I confirm the release match and usage rights and request a managed copy').check()
  await expect(dialog.getByLabel('Prefer the attached candidate (otherwise preserve the current preferred cover)')).not.toBeChecked()
  await attach.click()
  await expect.poll(() => attached).toBe(true)
  await dialog.getByRole('button', { name: 'Close artwork', exact: true }).click()
})

test('release cover management uses authenticated local images, survives reload and preserves explicit candidate lookup', async ({ page, request }) => {
  const errors: string[] = []
  const externalImages: string[] = []
  page.on('pageerror', e => errors.push(e.message))
  page.on('request', r => { if (r.resourceType() === 'image' && !r.url().startsWith('blob:') && !r.url().includes('localhost')) externalImages.push(r.url()) })
  await page.goto('/admin/#/releases/release-fixture/show')
  await page.getByLabel('API key').fill(key)
  await page.getByRole('button', { name: 'Connect', exact: true }).click()
  await expect(page.getByRole('heading', { name: 'Fixture Catalog Release', level: 1 })).toBeVisible()
  await expect(page.getByText('No preferred cover', { exact: true })).toBeVisible()
  await page.getByRole('button', { name: 'Change release artwork', exact: true }).click()
  const dialog = page.getByRole('dialog')
  await expect(dialog.getByRole('heading', { name: 'Release covers and gallery' })).toBeVisible()
  const bytes = await page.evaluate(() => {
    const canvas = document.createElement('canvas'); canvas.width = 32; canvas.height = 32
    const context = canvas.getContext('2d')!; context.fillStyle = '#1565c0'; context.fillRect(0, 0, 32, 32)
    return canvas.toDataURL('image/png').split(',')[1]
  })
  await dialog.getByLabel('Artwork file', { exact: true }).setInputFiles({ name: 'cover.png', mimeType: 'image/png', buffer: Buffer.from(bytes, 'base64') })
  await dialog.getByLabel('Upload caption', { exact: true }).fill('My release cover')
  await dialog.getByRole('textbox', { name: 'Usage rights', exact: true }).fill('Permission recorded')
  await dialog.getByLabel('Prefer this upload').check()
  await dialog.getByRole('button', { name: 'Upload image', exact: true }).click()
  await expect(dialog.getByRole('img', { name: 'My release cover', exact: true })).toHaveAttribute('src', /^blob:/)
  await dialog.getByRole('button', { name: 'Look up artwork candidates', exact: true }).click()
  await expect(dialog.getByRole('alert').filter({ hasText: 'No external artwork providers are configured.' })).toBeVisible()
  await dialog.getByRole('button', { name: 'Close artwork', exact: true }).click()
  const cover = page.getByRole('img', { name: 'Fixture Catalog Release cover', exact: true })
  await expect(cover).toHaveAttribute('src', /^blob:/)
  await page.getByRole('link', { name: 'Release Fixture Label (primary label)', exact: true }).click()
  const releases = page.getByRole('table', { name: 'Label releases', exact: true })
  const thumbnail = releases.getByRole('img', { name: 'Fixture Catalog Release cover thumbnail', exact: true })
  await expect(thumbnail).toHaveAttribute('src', /^blob:/)
  await expect(thumbnail).toHaveCSS('width', '40px')
  await expect(releases.getByRole('columnheader', { name: 'Primary artist', exact: true })).toBeVisible()
  await expect(releases.getByRole('row').nth(1)).toContainText('Fixture Composer')
  await expect(releases.getByRole('columnheader', { name: 'Label', exact: true })).toHaveCount(0)
  await thumbnail.click()
  await expect(cover).toHaveAttribute('src', /^blob:/)
  await cover.click()
  await expect(dialog.getByRole('heading', { name: 'Release covers and gallery' })).toBeVisible()
  await dialog.getByRole('button', { name: 'Close artwork', exact: true }).click()
  await page.reload()
  await page.getByLabel('API key').fill(key)
  await page.getByRole('button', { name: 'Connect', exact: true }).click()
  await expect(cover).toHaveAttribute('src', /^blob:/)
  const coverAction = page.getByRole('button', { name: 'Change release artwork', exact: true })
  await coverAction.focus()
  await page.keyboard.press('Enter')
  await expect(dialog.getByRole('heading', { name: 'Release covers and gallery' })).toBeVisible()
  await dialog.getByRole('button', { name: 'Close artwork', exact: true }).click()
  await expect(page.getByRole('button', { name: 'Manage release artwork', exact: true })).toHaveCount(0)
  await coverAction.click()
  const auth = { Authorization: `Bearer ${key}` }
  const first = await (await request.get('/api/v1/releases/release-fixture/artwork', { headers: auth })).json()
  await dialog.getByRole('button', { name: 'Replace image 1', exact: true }).click()
  await dialog.getByLabel('Artwork file', { exact: true }).setInputFiles({ name: 'replacement.png', mimeType: 'image/png', buffer: Buffer.from(bytes, 'base64') })
  await dialog.getByRole('textbox', { name: 'Usage rights', exact: true }).fill('Replacement permission')
  await dialog.getByRole('button', { name: 'Upload image', exact: true }).click()
  await expect(dialog.getByText('Image 1: replacement.png', { exact: true })).toBeVisible()
  const replaced = await (await request.get('/api/v1/releases/release-fixture/artwork', { headers: auth })).json()
  expect(replaced.items[0].id).toBe(first.items[0].id); expect(replaced.items[0].assetId).not.toBe(first.items[0].assetId)
  await dialog.getByRole('button', { name: 'Remove image 1', exact: true }).click()
  await dialog.getByRole('button', { name: 'Save gallery changes', exact: true }).click()
  await expect(dialog.getByRole('button', { name: 'Save gallery changes', exact: true })).toBeDisabled()
  await dialog.getByRole('button', { name: 'Close artwork', exact: true }).click()
  await expect(page.getByText('No preferred cover', { exact: true })).toBeVisible()
  expect((await request.get(first.items[0].originalUrl, { headers: auth })).status()).toBe(200)
  expect(externalImages).toEqual([]); expect(errors).toEqual([])
})
