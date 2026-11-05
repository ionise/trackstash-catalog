import { describe, expect, it, vi } from 'vitest'
import { createCatalogClient } from './catalog'

describe('Artist browsing transport', () => {
  it('passes search/label/role and pagination, escapes IDs and supports cancellation without writes', async () => {
    const fetcher = vi.fn<typeof fetch>()
    const { authProvider, artistClient } = createCatalogClient(fetcher)
    fetcher.mockResolvedValueOnce(Response.json({ items: [] }))
    await authProvider.login({ apiKey: 'test-key' })
    fetcher.mockClear()
    const signal = new AbortController().signal
    fetcher.mockResolvedValueOnce(Response.json({ items: [], total: 0, page: 2, pageSize: 25 }))
    await artistClient.list(new URLSearchParams({ labelId: 'label/one', role: 'remixer', q: 'A & B', page: '2' }), signal)
    expect(fetcher.mock.calls[0][0]).toBe('/api/v1/artists?labelId=label%2Fone&role=remixer&q=A+%26+B&page=2')
    fetcher.mockResolvedValueOnce(Response.json({ id: 'artist/one' }))
    await artistClient.get('artist/one', signal)
    expect(fetcher.mock.calls[1][0]).toBe('/api/v1/artists/artist%2Fone')
    fetcher.mockResolvedValueOnce(Response.json({ items: [] }))
    await artistClient.credits('artist/one', true, 3, signal)
    expect(fetcher.mock.calls[2][0]).toBe('/api/v1/artists/artist%2Fone/tracks?page=3&pageSize=25')
    fetcher.mockResolvedValueOnce(Response.json({ items: [] }))
    await artistClient.credits('artist/one', false, 2, signal)
    expect(fetcher.mock.calls[3][0]).toBe('/api/v1/artists/artist%2Fone/releases?page=2&pageSize=25')
    expect(fetcher.mock.calls.every(([, options]) => options?.method === undefined && options?.signal === signal)).toBe(true)
  })
  it('surfaces missing or gated Artist browsing without retries', async () => {
    const fetcher = vi.fn<typeof fetch>()
    const { authProvider, artistClient } = createCatalogClient(fetcher)
    fetcher.mockResolvedValueOnce(Response.json({ items: [] }))
    await authProvider.login({ apiKey: 'test-key' }); fetcher.mockClear()
    fetcher.mockResolvedValueOnce(Response.json({ detail: 'Catalog Artist was not found.', code: 'artist_not_found' }, { status: 404 }))
    await expect(artistClient.get('missing')).rejects.toMatchObject({ status: 404, message: 'Catalog Artist was not found.' })
    expect(fetcher).toHaveBeenCalledTimes(1)
  })
})
