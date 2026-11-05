import { describe, expect, it, vi } from 'vitest'
import { createCatalogClient } from './catalog'

describe('label relationship transport', () => {
  it('uses the original graph ETag, target versions and typed edge fields', async () => {
    const fetcher = vi.fn<typeof fetch>()
    const client = createCatalogClient(fetcher)
    fetcher.mockResolvedValueOnce(Response.json({ items: [], total: 0 }))
    await client.authProvider.login({ apiKey: 'test-key' })
    fetcher.mockResolvedValueOnce(Response.json({ labelId: 'child', items: [] }, { headers: { ETag: '"original"' } }))
    const graph = await client.relationshipsClient.get('child')
    const item = { id: null, toLabelId: 'parent', type: 'imprint_of' as const, source: 'manual', sourceUrl: null, notes: null, targetVersion: 'target-version' }
    fetcher.mockResolvedValueOnce(Response.json({ labelId: 'child', items: [] }, { headers: { ETag: '"updated"' } }))
    await client.relationshipsClient.save('child', graph.etag, [item])
    const [, init] = fetcher.mock.calls[2]
    expect(new Headers(init?.headers).get('If-Match')).toBe('"original"')
    expect(JSON.parse(String(init?.body))).toEqual({ items: [item] })
  })
  it('does not retry or hide relationship-specific stale errors', async () => {
    const fetcher = vi.fn<typeof fetch>()
    const client = createCatalogClient(fetcher)
    fetcher.mockResolvedValueOnce(Response.json({ items: [], total: 0 }))
    await client.authProvider.login({ apiKey: 'test-key' })
    fetcher.mockClear()
    fetcher.mockResolvedValueOnce(Response.json({ code: 'relationship_state_changed', detail: 'Related label changed. Review again.' }, { status: 412 }))
    await expect(client.relationshipsClient.save('child', '"old"', [])).rejects.toMatchObject({ status: 412, message: 'Related label changed. Review again.' })
    expect(fetcher).toHaveBeenCalledTimes(1)
  })
})
