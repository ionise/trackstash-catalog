import { describe, expect, it, vi } from 'vitest'
import { createCatalogClient } from './catalog'

describe('canonical release transport', () => {
  it('reads catalog releases with server-side label/search pagination and cancellation', async () => {
    const fetcher = vi.fn<typeof fetch>()
    const { authProvider, releaseClient } = createCatalogClient(fetcher)
    fetcher.mockResolvedValueOnce(Response.json({ items: [] }))
    await authProvider.login({ apiKey: 'test-key' })
    const signal = new AbortController().signal
    fetcher.mockResolvedValueOnce(Response.json({ items: [], total: 0, page: 2, pageSize: 25 }))
    await releaseClient.list(new URLSearchParams({ labelId: 'label & one', q: 'A & B', page: '2', pageSize: '25' }), signal)
    expect(fetcher.mock.calls[1][0]).toBe('/api/v1/releases?labelId=label+%26+one&q=A+%26+B&page=2&pageSize=25')
    expect(fetcher.mock.calls[1][1]).toMatchObject({ signal, cache: 'no-store', credentials: 'omit' })
    fetcher.mockResolvedValueOnce(Response.json({ id: 'release/one' }))
    await releaseClient.get('release/one', signal)
    expect(fetcher.mock.calls[2][0]).toBe('/api/v1/releases/release%2Fone')
    fetcher.mockResolvedValueOnce(Response.json({ items: [] }))
    await releaseClient.tracks('release/one', 3, signal)
    expect(fetcher.mock.calls[3][0]).toBe('/api/v1/releases/release%2Fone/tracks?page=3&pageSize=25')
    expect(fetcher.mock.calls.every(([, options]) => options?.method === undefined)).toBe(true)
  })
  it('surfaces missing releases without retry or an empty successful fallback', async () => {
    const fetcher = vi.fn<typeof fetch>()
    const { authProvider, releaseClient } = createCatalogClient(fetcher)
    fetcher.mockResolvedValueOnce(Response.json({ items: [] }))
    await authProvider.login({ apiKey: 'test-key' })
    fetcher.mockClear()
    fetcher.mockResolvedValueOnce(Response.json({ detail: 'Catalog release was not found.', code: 'release_not_found' }, { status: 404 }))
    await expect(releaseClient.get('missing')).rejects.toMatchObject({ status: 404, message: 'Catalog release was not found.' })
    expect(fetcher).toHaveBeenCalledTimes(1)
  })
})
