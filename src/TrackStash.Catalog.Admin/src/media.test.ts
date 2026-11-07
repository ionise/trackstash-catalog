import { expect, it, vi } from 'vitest'
import { createCatalogClient } from './catalog'

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
