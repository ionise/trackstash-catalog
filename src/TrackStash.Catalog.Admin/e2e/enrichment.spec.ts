import { expect, test } from '@playwright/test'

const key = 'test-only-browser-key-not-a-production-secret'

test('Recording enrichment is explicit, compares candidates without writes, and exposes errors and no matches', async ({ page }) => {
  let calls = 0
  const writes: string[] = []
  page.on('request', request => {
    if (request.url().includes('/api/v1/') && request.method() !== 'GET' && !request.url().endsWith('/enrichment/sonovault'))
      writes.push(request.url())
  })
  await page.route('**/api/v1/recordings/*/enrichment/sonovault', async route => {
    expect(route.request().method()).toBe('POST')
    calls++
    if (calls === 2) return route.fulfill({ status: 429, json: { detail: 'SonoVault quota exceeded. Try again later.', code: 'sonovault_rate_limited' } })
    await route.fulfill({ json: {
      providerId: 'sonovault', retrievedAt: '2026-11-07T00:00:00Z',
      query: { catalogId: 'release-recording-0', catalogVersion: 'snapshot-1', name: 'Catalog Track 1',
        mixName: 'Fixture Remix', isrc: 'RELTEST0', durationMs: 123456, artistNames: ['Fixture Composer'] },
      candidates: calls === 3 ? [] : [{
        providerId: 'sonovault', candidateId: '7', editionId: null, revision: null,
        match: { basis: 'Isrc', explanation: 'Candidate only, not accepted identity.', conflicts: ['Verify the remix version.'] },
        metadata: { name: 'SonoVault title', mixName: null, isrc: 'RELTEST0', durationMs: null, bpm: 124.5,
          musicalKey: { value: '10B', notation: 'Camelot' }, genre: 'House', genres: ['House'], subgenres: ['Progressive House'],
          credits: [], externalReferences: [{ source: 'sonovault', externalId: '7' }] },
        evidence: [{ field: 'Bpm', source: 'sonovault', sourceRecordId: '7', sourceUrl: null, retrievedAt: '2026-11-07T00:00:00Z', attribution: null }],
      }],
      warnings: ['Duration units are not verified.'],
    } })
  })
  await page.goto('/admin/#/recordings/release-recording-0/show')
  await page.getByLabel('API key').fill(key)
  await page.getByRole('button', { name: 'Connect', exact: true }).click()
  const section = page.getByRole('region', { name: 'SonoVault enrichment' })
  const button = section.getByRole('button', { name: 'Find enrichment on SonoVault' })
  await expect(button).toBeVisible()
  expect(calls).toBe(0)
  await button.click()
  const table = section.getByRole('table', { name: 'Enrichment comparison 7' })
  await expect(table.getByRole('row').filter({ hasText: 'Title' })).toContainText('Catalog Track 1')
  await expect(table).toContainText('SonoVault title')
  await expect(table).toContainText('124.5')
  await expect(table).toContainText('10B (Camelot)')
  await expect(table.getByRole('row').filter({ has: page.getByRole('cell', { name: 'Genre', exact: true }) })).toContainText('House')
  await expect(table.getByRole('row').filter({ has: page.getByRole('cell', { name: 'Subgenre', exact: true }) })).toContainText('Progressive House')
  await expect(table).toContainText('No proposal')
  await expect(section.getByText('Verify the remix version.')).toBeVisible()
  await button.click()
  await expect(section.getByRole('alert').filter({ hasText: 'SonoVault quota exceeded' })).toBeVisible()
  await expect(table).toHaveCount(0)
  await button.click()
  await expect(section.getByText(/No SonoVault candidates found/)).toBeVisible()
  expect(calls).toBe(3)
  expect(writes).toEqual([])
})
