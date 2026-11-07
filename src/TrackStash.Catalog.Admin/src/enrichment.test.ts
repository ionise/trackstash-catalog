import { expect, it, vi } from 'vitest'
import { createCatalogClient } from './catalog'

it('only sends an explicit authenticated lookup action for the Recording ID and forwards cancellation', async () => {
  const fetcher = vi.fn<typeof fetch>().mockResolvedValue(Response.json({ items: [] }))
  const { authProvider, trackClient } = createCatalogClient(fetcher)
  await authProvider.login({ apiKey: 'test-catalog-key' })
  fetcher.mockClear()
  const controller = new AbortController()
  await trackClient.enrichment('recording /1', controller.signal)
  expect(fetcher).toHaveBeenCalledTimes(1)
  expect(fetcher.mock.calls[0][0]).toBe('/api/v1/recordings/recording%20%2F1/enrichment/sonovault')
  expect(fetcher.mock.calls[0][1]?.method).toBe('POST')
  expect(fetcher.mock.calls[0][1]?.signal).toBe(controller.signal)
  expect(fetcher.mock.calls[0][1]?.body).toBeUndefined()
  expect(new Headers(fetcher.mock.calls[0][1]?.headers).has('x-api-key')).toBe(false)
})

it.each(['artists', 'labels', 'releases'] as const)('sends only the %s identity and forwards cancellation', async resource => {
  const fetcher = vi.fn<typeof fetch>().mockResolvedValue(Response.json({}))
  const { authProvider, enrichmentClient } = createCatalogClient(fetcher)
  await authProvider.login({ apiKey: 'test-catalog-key' })
  fetcher.mockClear()
  const controller = new AbortController()
  await enrichmentClient.lookup(resource, 'entity /1', controller.signal)
  expect(fetcher).toHaveBeenCalledTimes(1)
  expect(fetcher.mock.calls[0][0]).toBe(`/api/v1/${resource}/entity%20%2F1/enrichment/sonovault`)
  expect(fetcher.mock.calls[0][1]?.method).toBe('POST')
  expect(fetcher.mock.calls[0][1]?.body).toBeUndefined()
  expect(fetcher.mock.calls[0][1]?.signal).toBe(controller.signal)
  expect(new Headers(fetcher.mock.calls[0][1]?.headers).has('x-api-key')).toBe(false)
})

it('explicit Release candidate inspection encodes IDs, selects an edition, and sends no metadata body', async () => {
  const fetcher = vi.fn<typeof fetch>().mockImplementation(async () => Response.json({}))
  const { authProvider, enrichmentClient } = createCatalogClient(fetcher)
  await authProvider.login({ apiKey: 'test-catalog-key' })
  fetcher.mockClear()
  const controller = new AbortController()
  await enrichmentClient.releaseCandidate('release /1', '8', '12', controller.signal)
  expect(fetcher.mock.calls[0][0]).toBe('/api/v1/releases/release%20%2F1/enrichment/sonovault/candidates/8?editionId=12')
  expect(fetcher.mock.calls[0][1]?.method).toBe('POST')
  expect(fetcher.mock.calls[0][1]?.signal).toBe(controller.signal)
  expect(fetcher.mock.calls[0][1]?.body).toBeUndefined()
  await enrichmentClient.releaseCandidate('release /1', '8')
  expect(fetcher.mock.calls[1][0]).toBe('/api/v1/releases/release%20%2F1/enrichment/sonovault/candidates/8')
})

it('Beatport lookup and detail use the selected provider without sharing SonoVault credentials', async () => {
  const fetcher = vi.fn<typeof fetch>().mockImplementation(async () => Response.json({}))
  const { authProvider, enrichmentClient } = createCatalogClient(fetcher)
  await authProvider.login({ apiKey: 'test-catalog-key' })
  fetcher.mockClear()
  await enrichmentClient.lookup('releases', 'release-1', undefined, 'beatport')
  await enrichmentClient.releaseCandidate('release-1', '10', undefined, undefined, 'beatport')
  expect(fetcher.mock.calls.map(call => call[0])).toEqual([
    '/api/v1/releases/release-1/enrichment/beatport',
    '/api/v1/releases/release-1/enrichment/beatport/candidates/10',
  ])
  expect(fetcher.mock.calls.every(call => call[1]?.method === 'POST' && call[1]?.body === undefined)).toBe(true)
})

it('review preparation sends only identity and original catalog version; apply sends selected intent and original review ETag', async () => {
  const fetcher = vi.fn<typeof fetch>().mockImplementation(async () => Response.json({ id: 'review /1' }, { headers: { ETag: '"original-review"' } }))
  const { authProvider, enrichmentClient } = createCatalogClient(fetcher)
  await authProvider.login({ apiKey: 'test-catalog-key' })
  fetcher.mockClear()
  const controller = new AbortController()
  const review = await enrichmentClient.prepare('recordings', 'recording /1', '7', null, 'catalog-original', controller.signal)
  expect(fetcher.mock.calls[0][0]).toBe('/api/v1/recordings/recording%20%2F1/enrichment/sonovault/reviews')
  expect(JSON.parse(String(fetcher.mock.calls[0][1]?.body))).toEqual({ candidateId: '7', editionId: null, catalogVersion: 'catalog-original' })
  expect(fetcher.mock.calls[0][1]?.signal).toBe(controller.signal)
  await enrichmentClient.apply(review, ['Genre', 'Subgenre', 'PreferredGenre'], 'Progressive House', 'Reviewed match', true, controller.signal)
  expect(fetcher.mock.calls[1][0]).toBe('/api/v1/enrichment-reviews/review%20%2F1/apply')
  expect(new Headers(fetcher.mock.calls[1][1]?.headers).get('If-Match')).toBe('"original-review"')
  expect(JSON.parse(String(fetcher.mock.calls[1][1]?.body))).toEqual({
    fields: ['Genre', 'Subgenre', 'PreferredGenre'], preferredGenre: 'Progressive House', reason: 'Reviewed match', confirmed: true,
  })
})

it('refuses an unversioned review and preserves enrichment-specific stale-state errors', async () => {
  let status = 200
  const fetcher = vi.fn<typeof fetch>().mockImplementation(async () => Response.json(status === 200 ? {} :
    { code: 'enrichment_state_changed', detail: 'The catalog changed. Prepare a fresh review.' }, { status }))
  const { authProvider, enrichmentClient } = createCatalogClient(fetcher)
  await authProvider.login({ apiKey: 'test-catalog-key' })
  await expect(enrichmentClient.prepare('releases', 'r', '8', '12', 'old')).rejects.toThrow('missing its ETag')
  status = 412
  await expect(enrichmentClient.prepare('releases', 'r', '8', '12', 'old')).rejects.toThrow('The catalog changed. Prepare a fresh review.')
})

it.each(['recordings', 'releases'] as const)('prepares a Beatport %s review without client metadata or a SonoVault request', async resource => {
  const fetcher = vi.fn<typeof fetch>().mockImplementation(async () => Response.json({ id: 'beatport-review' },
    { headers: { ETag: '"frozen-beatport"' } }))
  const { authProvider, enrichmentClient } = createCatalogClient(fetcher)
  await authProvider.login({ apiKey: 'test-catalog-key' })
  fetcher.mockClear()
  const controller = new AbortController()
  await enrichmentClient.lookup(resource, 'entity /1', controller.signal, 'beatport')
  const review = await enrichmentClient.prepare(resource, 'entity /1', '10', null, 'lookup-version', controller.signal, 'beatport')
  await enrichmentClient.apply(review, ['Name', 'BeatportIdentity'], null, 'Reviewed Beatport match', true, controller.signal)
  expect(fetcher.mock.calls.map(call => call[0])).toEqual([
    `/api/v1/${resource}/entity%20%2F1/enrichment/beatport`,
    `/api/v1/${resource}/entity%20%2F1/enrichment/beatport/reviews`,
    '/api/v1/enrichment-reviews/beatport-review/apply',
  ])
  expect(JSON.parse(String(fetcher.mock.calls[1][1]?.body))).toEqual({ candidateId: '10', editionId: null, catalogVersion: 'lookup-version' })
  expect(fetcher.mock.calls.every(call => call[1]?.signal === controller.signal)).toBe(true)
  expect(new Headers(fetcher.mock.calls[2][1]?.headers).get('If-Match')).toBe('"frozen-beatport"')
})
