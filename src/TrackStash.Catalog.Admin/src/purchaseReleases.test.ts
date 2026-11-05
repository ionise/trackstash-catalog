import { describe, expect, it, vi } from 'vitest'
import { createCatalogClient } from './catalog'

describe('purchase release inspector transport', () => {
  it('uses authenticated read-only paged requests with cancellation and literal search encoding', async () => {
    const fetcher = vi.fn<typeof fetch>()
    const { authProvider, purchaseReleaseClient } = createCatalogClient(fetcher)
    fetcher.mockResolvedValueOnce(Response.json({ items: [] }))
    await authProvider.login({ apiKey: 'test-key' })
    const abort = new AbortController()
    fetcher.mockResolvedValueOnce(Response.json({ items: [], total: 0, page: 2, pageSize: 25 }))
    await purchaseReleaseClient.list(2, 'A & B', abort.signal)
    expect(fetcher.mock.calls[1][0]).toBe('/api/v1/purchase-release-candidates?page=2&pageSize=25&q=A+%26+B')
    expect(fetcher.mock.calls[1][1]).toMatchObject({ signal: abort.signal, cache: 'no-store', credentials: 'omit' })
    expect(new Headers(fetcher.mock.calls[1][1]?.headers).get('Authorization')).toBe('Bearer test-key')
    fetcher.mockResolvedValueOnce(Response.json({ source: { releaseId: 10 } }))
    await purchaseReleaseClient.inspect(10, 3, abort.signal)
    expect(fetcher.mock.calls[2][0]).toBe('/api/v1/purchase-release-candidates/10?page=3&pageSize=25')
    expect(fetcher.mock.calls[2][1]?.method).toBeUndefined()
  })
  it('surfaces unavailable source errors without retries', async () => {
    const fetcher = vi.fn<typeof fetch>()
    const { authProvider, purchaseReleaseClient } = createCatalogClient(fetcher)
    fetcher.mockResolvedValueOnce(Response.json({ items: [] }))
    await authProvider.login({ apiKey: 'test-key' })
    fetcher.mockClear()
    fetcher.mockResolvedValueOnce(Response.json({ detail: 'Purchase source unavailable', code: 'purchase_source_unavailable' }, { status: 503 }))
    await expect(purchaseReleaseClient.list(1, '')).rejects.toMatchObject({ status: 503, message: 'Purchase source unavailable' })
    expect(fetcher).toHaveBeenCalledTimes(1)
  })
})
