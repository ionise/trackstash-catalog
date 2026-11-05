import { describe, expect, it, vi } from 'vitest'
import { createCatalogClient } from './catalog'

describe('group transport', () => {
  it('preserves membership tokens and endpoint versions for conditional edits and removal', async () => {
    const fetcher = vi.fn<typeof fetch>()
    const { authProvider, groupClient } = createCatalogClient(fetcher)
    fetcher.mockResolvedValueOnce(Response.json({ items: [], total: 0 }))
    await authProvider.login({ apiKey: 'test-key' })
    const fields = { status: 'current' as const, since: '2020', until: null, source: 'manual', sourceUrl: null, notes: null, labelVersion: 'label-v', groupVersion: 'group-v', confirmRejoin: false }
    fetcher.mockResolvedValueOnce(Response.json({ id: 'membership' }, { headers: { ETag: '"updated"' } }))
    await groupClient.saveMembership('membership', '"original"', fields)
    const [url, request] = fetcher.mock.calls[1]
    expect(url).toBe('/api/v1/label-group-memberships/membership')
    expect(new Headers(request?.headers).get('If-Match')).toBe('"original"')
    expect(JSON.parse(String(request?.body))).toEqual(fields)
    fetcher.mockResolvedValueOnce(new Response(null, { status: 204 }))
    await groupClient.removeMembership('membership', '"updated"')
    expect(fetcher.mock.calls[2][1]?.method).toBe('DELETE')
    expect(new Headers(fetcher.mock.calls[2][1]?.headers).get('If-Match')).toBe('"updated"')
  })
  it('retains group-specific stale errors and does not retry', async () => {
    const fetcher = vi.fn<typeof fetch>()
    const { authProvider, groupClient } = createCatalogClient(fetcher)
    fetcher.mockResolvedValueOnce(Response.json({ items: [], total: 0 }))
    await authProvider.login({ apiKey: 'test-key' })
    fetcher.mockClear()
    fetcher.mockResolvedValueOnce(Response.json({ code: 'group_state_changed', detail: 'Group changed. No changes were saved.' }, { status: 412 }))
    await expect(groupClient.update('g', '"old"', { name: 'Renamed', sortName: null, description: null, website: null }))
      .rejects.toMatchObject({ status: 412, message: 'Group changed. No changes were saved.' })
    expect(fetcher).toHaveBeenCalledTimes(1)
  })
})
