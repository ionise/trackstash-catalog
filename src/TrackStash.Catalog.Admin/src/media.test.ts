import { expect, it, vi } from 'vitest'
import { createCatalogClient, mediaLibraryName } from './catalog'

it('media verification sends only server-derived identity, then original ETag and explicit audit intent', async () => {
  const fetcher = vi.fn<typeof fetch>().mockImplementation(async () => Response.json({ id: 'review /1' }, { headers: { ETag: '"verified-review"' } }))
  const { authProvider, mediaClient } = createCatalogClient(fetcher)
  await authProvider.login({ apiKey: 'test-catalog-key' })
  fetcher.mockClear()
  const controller = new AbortController()
  const review = await mediaClient.prepare(42, 'recording-1', controller.signal)
  expect(fetcher.mock.calls[0][0]).toBe('/api/v1/media/42/link-reviews')
  expect(JSON.parse(String(fetcher.mock.calls[0][1]?.body))).toEqual({ recordingId: 'recording-1' })
  expect(fetcher.mock.calls[0][1]?.signal).toBe(controller.signal)
  await mediaClient.apply(review, 'Confirmed version identity', true, controller.signal)
  expect(fetcher.mock.calls[1][0]).toBe('/api/v1/media-link-reviews/review%20%2F1/apply')
  expect(new Headers(fetcher.mock.calls[1][1]?.headers).get('If-Match')).toBe('"verified-review"')
  expect(JSON.parse(String(fetcher.mock.calls[1][1]?.body))).toEqual({ reason: 'Confirmed version identity', confirmed: true })
})

it('media review without an ETag cannot become writable', async () => {
  const fetcher = vi.fn<typeof fetch>().mockResolvedValue(Response.json({ id: 'review-1' }))
  const { authProvider, mediaClient } = createCatalogClient(fetcher)
  await authProvider.login({ apiKey: 'test-catalog-key' })
  await expect(mediaClient.prepare(1, 'recording-1')).rejects.toThrow('missing its ETag')
})

it('media inventory queries and Recording links are authenticated scoped reads with cancellation', async () => {
  const fetcher = vi.fn<typeof fetch>().mockImplementation(async () => Response.json([]))
  const { authProvider, mediaClient } = createCatalogClient(fetcher)
  await authProvider.login({ apiKey: 'test-catalog-key' })
  fetcher.mockClear()
  const signal = new AbortController().signal
  await mediaClient.list(new URLSearchParams({ library: 'Dropbox', q: 'Test mix', page: '2' }), signal)
  await mediaClient.candidates(42, signal)
  await mediaClient.linked('recording /1', signal)
  expect(fetcher.mock.calls.map(call => call[0])).toEqual([
    '/api/v1/media?library=Dropbox&q=Test+mix&page=2',
    '/api/v1/media/42/candidates',
    '/api/v1/recordings/recording%20%2F1/media',
  ])
  expect(fetcher.mock.calls.every(call => call[1]?.signal === signal && call[1]?.body === undefined)).toBe(true)
})

it('media metadata grouping, filters and sort travel together and configured library names remain presentation-only', async () => {
  const fetcher = vi.fn<typeof fetch>().mockImplementation(async () => Response.json({}))
  const { authProvider, mediaClient } = createCatalogClient(fetcher)
  await authProvider.login({ apiKey: 'test-catalog-key' })
  fetcher.mockClear()
  const query = new URLSearchParams({ groupBy: 'release', groupKey: 'opaque-group', label: 'Test Label',
    artist: 'Artist', release: 'Album', track: 'Tune', sort: 'artist', direction: 'desc', library: 'JetDrive', page: '2' })
  await mediaClient.list(query)
  expect(fetcher.mock.calls[0][0]).toBe(`/api/v1/media?${query}`)
  const names = { JetDrive: 'Travel library', Dropbox: 'DJ selections', NAS: 'Archive' }
  expect(['JetDrive', 'Dropbox', 'NAS', 'Other'].map(id => mediaLibraryName(id, names))).toEqual(['Travel library', 'DJ selections', 'Archive', 'Other'])
  expect(mediaLibraryName('JetDrive')).toBe('JetDrive')
})

it('library settings retain stable IDs and require original ETag and explicit root approval', async () => {
  const fetcher = vi.fn<typeof fetch>().mockImplementation(async () => Response.json({ libraries: [], version: 'v1' }, { headers: { ETag: '"settings-original"' } }))
  const { authProvider, mediaClient } = createCatalogClient(fetcher)
  await authProvider.login({ apiKey: 'test-catalog-key' })
  fetcher.mockClear()
  const signal = new AbortController().signal
  const settings = await mediaClient.libraries(signal)
  const libraries = [{ id: 'fixed-id', name: 'New library name', root: '/Volumes/Other/Library', enabled: true }]
  await mediaClient.saveLibraries(settings, libraries, true, signal)
  expect(fetcher.mock.calls[1][0]).toBe('/api/v1/media-libraries')
  expect(fetcher.mock.calls[1][1]?.method).toBe('PATCH')
  expect(new Headers(fetcher.mock.calls[1][1]?.headers).get('If-Match')).toBe('"settings-original"')
  expect(JSON.parse(String(fetcher.mock.calls[1][1]?.body))).toEqual({ libraries, confirmed: true })
  expect(fetcher.mock.calls.every(call => call[1]?.signal === signal)).toBe(true)
})

it('Release-group association requires original ETag and sends no client metadata or media paths', async () => {
  const fetcher = vi.fn<typeof fetch>().mockImplementation(async () => Response.json({ id: 'release review/1' }, { headers: { ETag: '"group-original"' } }))
  const { authProvider, mediaClient } = createCatalogClient(fetcher)
  await authProvider.login({ apiKey: 'test-catalog-key' })
  fetcher.mockClear()
  const signal = new AbortController().signal
  const review = await mediaClient.prepareRelease('group-key', 'release-1', signal)
  expect(fetcher.mock.calls[0][0]).toBe('/api/v1/media-release-reviews')
  expect(JSON.parse(String(fetcher.mock.calls[0][1]?.body))).toEqual({ groupKey: 'group-key', releaseId: 'release-1' })
  await mediaClient.applyRelease(review, 'Reviewed full group', true, signal)
  expect(fetcher.mock.calls[1][0]).toBe('/api/v1/media-release-reviews/release%20review%2F1/apply')
  expect(new Headers(fetcher.mock.calls[1][1]?.headers).get('If-Match')).toBe('"group-original"')
  expect(JSON.parse(String(fetcher.mock.calls[1][1]?.body))).toEqual({ reason: 'Reviewed full group', confirmed: true })
  expect(fetcher.mock.calls.every(call => call[1]?.signal === signal)).toBe(true)
})

it('library settings stale saves show library-specific reload guidance, not label editing guidance', async () => {
  const fetcher = vi.fn<typeof fetch>().mockImplementation(async () => Response.json({ libraries: [], version: 'v1' }, { headers: { ETag: '"original"' } }))
  const { authProvider, mediaClient } = createCatalogClient(fetcher)
  await authProvider.login({ apiKey: 'test-catalog-key' })
  const settings = await mediaClient.libraries()
  fetcher.mockResolvedValueOnce(Response.json({ code: 'media_settings_changed' }, { status: 412 }))
  await expect(mediaClient.saveLibraries(settings, [], true)).rejects.toThrow('Library settings changed; your edits were not saved. Reload saved settings before saving again.')
})

it('Release-group association rejects a preparation response without an ETag', async () => {
  const fetcher = vi.fn<typeof fetch>().mockImplementation(async () => Response.json({ id: 'review' }))
  const { authProvider, mediaClient } = createCatalogClient(fetcher)
  await authProvider.login({ apiKey: 'test-catalog-key' })
  await expect(mediaClient.prepareRelease('group', 'release')).rejects.toThrow('missing its ETag')
})

it('reconciliation sends bounded explicit selections, original proposal/review ETags and one audit confirmation', async () => {
  const fetcher = vi.fn<typeof fetch>().mockImplementation(async () => Response.json({ id: 'batch /1', groupKey: 'group-key', releaseId: 'release-1' },
    { headers: { ETag: '"original-proposal"' } }))
  const { authProvider, mediaClient } = createCatalogClient(fetcher)
  await authProvider.login({ apiKey: 'test-catalog-key' })
  fetcher.mockClear()
  const signal = new AbortController().signal
  const plan = await mediaClient.reconcile('group-key', 'release-1', signal)
  expect(fetcher.mock.calls[0][0]).toBe('/api/v1/media-reconciliation?groupKey=group-key&releaseId=release-1')
  fetcher.mockResolvedValueOnce(Response.json({ id: 'batch /1' }, { headers: { ETag: '"original-batch"' } }))
  const review = await mediaClient.prepareReconciliation(plan, [{ mediaId: 42, recordingId: 'recording-1' }], signal)
  expect(new Headers(fetcher.mock.calls[1][1]?.headers).get('If-Match')).toBe('"original-proposal"')
  expect(JSON.parse(String(fetcher.mock.calls[1][1]?.body))).toEqual({
    groupKey: 'group-key', releaseId: 'release-1', selections: [{ mediaId: 42, recordingId: 'recording-1' }],
  })
  await mediaClient.applyReconciliation(review, 'Reviewed selected versions', true, signal)
  expect(fetcher.mock.calls[2][0]).toBe('/api/v1/media-reconciliation-reviews/batch%20%2F1/apply')
  expect(new Headers(fetcher.mock.calls[2][1]?.headers).get('If-Match')).toBe('"original-batch"')
  expect(JSON.parse(String(fetcher.mock.calls[2][1]?.body))).toEqual({ reason: 'Reviewed selected versions', confirmed: true })
  expect(fetcher.mock.calls.every(call => call[1]?.signal === signal)).toBe(true)
})

it('reconciliation proposals without ETags cannot be verified', async () => {
  const fetcher = vi.fn<typeof fetch>().mockResolvedValue(Response.json({}))
  const { authProvider, mediaClient } = createCatalogClient(fetcher)
  await authProvider.login({ apiKey: 'test-catalog-key' })
  await expect(mediaClient.reconcile('group-key', 'release-1')).rejects.toThrow('missing their ETag')
})
