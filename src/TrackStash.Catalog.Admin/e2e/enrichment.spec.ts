import { expect, test } from '@playwright/test'

const key = 'test-only-browser-key-not-a-production-secret'

test('Release metadata provider choice is explicit and switches comparisons without automatic requests', async ({ page }) => {
  let calls = 0
  await page.route('**/api/v1/releases/*/enrichment/beatport**', route => {
    calls++
    return route.fulfill({ json: {
      providerId: 'beatport', retrievedAt: '2026-11-07T00:00:00Z',
      query: { catalogId: 'release-fixture', catalogVersion: 'version-1', name: 'Fixture Catalog Release',
        artistNames: ['Fixture Composer'], labelNames: ['Release Fixture Label'], externalReferences: [] },
      candidates: [{
        providerId: 'beatport', candidateId: '10', editionId: null, revision: null,
        match: { basis: 'MetadataSearch', explanation: 'Candidate only.', conflicts: [] },
        metadata: { name: 'Beatport release title', labelName: 'Release Fixture Label', catalogNumber: 'CAT10',
          credits: [], externalReferences: [{ source: 'beatport', externalId: '10' }],
          releaseDetails: { reportedReleaseDate: '2026-10-01', selectedEditionId: null, editions: [], tracks: [] } },
        evidence: [],
      }],
      warnings: ['Metadata only, no artwork attached.'],
    } })
  })
  await page.goto('/admin/#/releases/release-fixture/show?tab=Details')
  await page.getByLabel('API key').fill(key)
  await page.getByRole('button', { name: 'Connect', exact: true }).click()
  const provider = page.getByRole('combobox', { name: 'Enrichment provider' })
  await provider.click()
  await page.getByRole('option', { name: 'Beatport', exact: true }).click()
  const section = page.getByRole('region', { name: 'Beatport enrichment' })
  expect(calls).toBe(0)
  await section.getByRole('button', { name: 'Find enrichment on Beatport' }).click()
  await expect(section.getByRole('table')).toContainText('Beatport release title')
  await expect(section.getByRole('table')).toContainText('Beatport proposal')
  await expect(section).not.toContainText('Provider group date')
  await expect(section).not.toContainText('MusicBrainz release IDs')
  await section.getByRole('button', { name: 'Fetch details for candidate 10' }).click()
  expect(calls).toBe(2)
  await provider.click()
  await page.getByRole('option', { name: 'SonoVault', exact: true }).click()
  await expect(page.getByRole('region', { name: 'SonoVault enrichment' }).getByRole('table')).toHaveCount(0)
  expect(calls).toBe(2)
})

test('Release enrichment compares known metadata and explicitly inspects editions without writes', async ({ page }) => {
  await page.setViewportSize({ width: 420, height: 900 })
  let calls = 0
  const writes: string[] = []
  page.on('request', request => {
    if (request.url().includes('/api/v1/') && request.method() !== 'GET' && !request.url().includes('/enrichment/sonovault'))
      writes.push(request.url())
  })
  await page.route('**/api/v1/releases/*/enrichment/sonovault**', route => {
    calls++
    expect(route.request().method()).toBe('POST')
    expect(route.request().postData()).toBeNull()
    if (calls === 4) return route.fulfill({ status: 429, json: { detail: 'SonoVault quota exceeded.', code: 'sonovault_rate_limited' } })
    const url = new URL(route.request().url())
    const detail = url.pathname.endsWith('/candidates/8')
    const edition = url.searchParams.get('editionId')
    if (calls === 2) expect(detail).toBe(true)
    if (calls === 3) expect(edition).toBe('12')
    const editions = [
      { id: '12', source: 'discogs', externalId: '99', title: 'Digital edition', format: 'digital',
        releaseDate: '2024-01-01', datePrecision: 'year', labelName: 'Release Fixture Label', catalogNumber: 'CAT1', barcode: '123', trackCount: 1 },
    ]
    return route.fulfill({ json: {
      providerId: 'sonovault', retrievedAt: '2026-11-07T00:00:00Z',
      query: { catalogId: 'release-fixture', catalogVersion: 'release-snapshot', name: 'Catalog release',
        artistNames: ['Fixture Composer'], labelNames: ['Release Fixture Label'], catalogNumber: 'CAT1',
        catalogPublishDate: '2024-02-03', externalReferences: [], lookupCandidateId: detail ? '8' : null },
      candidates: calls === 5 ? [] : [{
        providerId: 'sonovault', candidateId: '8', editionId: edition, revision: null,
        match: { basis: detail ? 'ExternalReference' : 'MetadataSearch', explanation: 'Candidate compared with known artists and labels.', conflicts: [] },
        metadata: { name: 'Catalog release', labelName: 'Release Fixture Label', catalogNumber: edition ? 'CAT1' : null,
          credits: [{ name: 'Fixture Composer', role: 'primary' }], externalReferences: [{ source: 'sonovault', externalId: '8' }],
          releaseDetails: { reportedReleaseDate: '2020-01-01', selectedEditionId: edition,
            musicBrainzReleaseIds: ['edition-mbid'], musicBrainzReleaseGroupIds: ['group-mbid'],
            editions: detail ? editions : [],
            tracks: detail ? [{ id: '7', title: 'Provider track (Remix)', isrc: 'GBTEST000001', discNumber: 1, trackNumber: edition ? 1 : 2,
              credits: [{ name: 'Fixture Composer', role: 'primary' }] }] : [],
          } },
        evidence: [{ field: 'ReleaseDetails', source: 'sonovault', sourceRecordId: '8', retrievedAt: '2026-11-07T00:00:00Z' }],
      }],
      warnings: ['Different editions may share this Release ID. No ownership is inferred.'],
    } })
  })
  await page.goto('/admin/#/releases/release-fixture/show')
  await page.getByLabel('API key').fill(key)
  await page.getByRole('button', { name: 'Connect', exact: true }).click()
  await page.getByRole('tab', { name: 'Details', exact: true }).click()
  const section = page.getByRole('region', { name: 'SonoVault enrichment' })
  const lookup = section.getByRole('button', { name: 'Find enrichment on SonoVault' })
  await expect(lookup).toBeVisible()
  expect(calls).toBe(0)
  await lookup.click()
  const comparison = section.getByRole('table', { name: 'Enrichment comparison 8' })
  await expect(comparison).toContainText('Fixture Composer')
  await expect(comparison).toContainText('Release Fixture Label')
  await expect(comparison).toContainText('Provider group date (not an edition date)')
  await expect(section.getByRole('table', { name: 'SonoVault tracklist 8' })).toHaveCount(0)
  expect(calls).toBe(1)
  await section.getByRole('button', { name: 'Fetch details for candidate 8' }).click()
  await expect(section.getByRole('table', { name: 'SonoVault editions 8' })).toContainText('year')
  await expect(section.getByText('Provider consensus tracklist (no edition selected)')).toBeVisible()
  await expect(section.getByRole('table', { name: 'SonoVault tracklist 8' })).toContainText('Provider track (Remix)')
  expect(calls).toBe(2)
  await section.getByRole('button', { name: 'Inspect edition 12' }).click()
  await expect(section.getByText('Edition 12 tracklist', { exact: true })).toBeVisible()
  await expect(section.getByRole('button', { name: 'Inspect edition 12' })).toBeDisabled()
  expect(calls).toBe(3)
  await page.getByRole('tab', { name: 'Tracks', exact: true }).click()
  await expect(section).toBeHidden()
  await page.getByRole('tab', { name: 'Details', exact: true }).click()
  await expect(section.getByText('Edition 12 tracklist', { exact: true })).toBeVisible()
  expect(calls).toBe(3)
  await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true)
  await section.getByRole('button', { name: 'Fetch details for candidate 8' }).click()
  await expect(section.getByRole('alert').filter({ hasText: 'SonoVault quota exceeded.' })).toBeVisible()
  await expect(section.getByText('Edition 12 tracklist', { exact: true })).toBeVisible()
  await lookup.click()
  await expect(section.getByText(/No SonoVault candidates found/)).toBeVisible()
  expect(calls).toBe(5)
  expect(writes).toEqual([])
})

for (const [resource, id, name] of [
  ['artists', 'release-fixture-artist', 'Fixture Composer'],
  ['labels', 'release-fixture-label', 'Release Fixture Label'],
]) {
  test(`${resource} enrichment is explicit, shows identity proposals and never writes`, async ({ page }) => {
    let calls = 0
    const writes: string[] = []
    page.on('request', request => {
      if (request.url().includes('/api/v1/') && request.method() !== 'GET' && !request.url().endsWith('/enrichment/sonovault'))
        writes.push(request.url())
    })
    await page.route(`**/api/v1/${resource}/*/enrichment/sonovault`, route => {
      calls++
      return route.fulfill({ json: {
        providerId: 'sonovault', retrievedAt: '2026-11-07T00:00:00Z',
        query: { catalogId: id, catalogVersion: 'entity-snapshot-1', name, mixName: null,
          isrc: null, durationMs: null, artistNames: [], externalReferences: [{ source: 'beatport', externalId: '12345' }] },
        candidates: calls === 2 ? [] : [{
          providerId: 'sonovault', candidateId: '8', editionId: null, revision: null,
          match: { basis: 'MetadataSearch', explanation: 'Name search is not accepted identity.', conflicts: ['Review entity identity.'] },
          metadata: { name: 'Provider name', country: 'United Kingdom', formationYear: 1980, formationDate: '1980-02-03',
            socialLinks: { instagram: 'artist-handle' }, externalReferences: [{ source: 'sonovault', externalId: '8' }, { source: 'musicbrainz', externalId: 'mbid' }] },
          evidence: [{ field: 'Name', source: 'sonovault', sourceRecordId: '8', retrievedAt: '2026-11-07T00:00:00Z' }],
        }],
        warnings: ['Read-only entity pilot.'],
      } })
    })
    await page.goto(`/admin/#/${resource}/${id}/show`)
    await page.getByLabel('API key').fill(key)
    await page.getByRole('button', { name: 'Connect', exact: true }).click()
    await page.getByRole('tab', { name: 'Details', exact: true }).click()
    const section = page.getByRole('region', { name: 'SonoVault enrichment' })
    const button = section.getByRole('button', { name: 'Find enrichment on SonoVault' })
    await expect(button).toBeVisible()
    expect(calls).toBe(0)
    await button.click()
    const table = section.getByRole('table', { name: 'Enrichment comparison 8' })
    await expect(table).toContainText(name)
    await expect(table).toContainText('Provider name')
    await expect(table).toContainText('sonovault: 8')
    await expect(table).toContainText('beatport: 12345')
    await expect(section.getByText('Review entity identity.')).toBeVisible()
    if (resource === 'artists') {
      await expect(table).toContainText('United Kingdom')
      await expect(table).toContainText('Start / birth year')
      await expect(table).toContainText('1980')
      await expect(table).toContainText('artist-handle')
      await expect(table.getByRole('link')).toHaveCount(0)
    } else {
      await expect(table).not.toContainText('Country of origin')
      await expect(table).not.toContainText('BPM')
    }
    await button.click()
    await expect(section.getByText(/No SonoVault candidates found/)).toBeVisible()
    expect(calls).toBe(2)
    expect(writes).toEqual([])
  })
}

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
