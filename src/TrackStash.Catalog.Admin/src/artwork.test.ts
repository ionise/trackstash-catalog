import { describe, expect, it, vi } from 'vitest'
import { createCatalogClient } from './catalog'

async function setup() {
  const fetcher = vi.fn<typeof fetch>()
  const client = createCatalogClient(fetcher)
  fetcher.mockResolvedValueOnce(Response.json({ items: [], total: 0 }))
  await client.authProvider.login({ apiKey: 'test-key' })
  fetcher.mockClear()
  return { ...client, fetcher }
}

describe('protected artwork', () => {
  it('uploads multipart without a forged boundary and retains the original gallery ETag', async () => {
    const { artworkClient, fetcher } = await setup()
    const form = new FormData()
    form.set('usageRights', 'Permission recorded')
    form.set('file', new Blob(['image']), 'logo.png')
    fetcher.mockResolvedValueOnce(Response.json({ labelId: 'one', items: [] }, { headers: { ETag: '"new"' } }))
    const saved = await artworkClient.upload('one', '"original"', form)
    const headers = new Headers(fetcher.mock.calls[0][1]?.headers)
    expect(headers.get('If-Match')).toBe('"original"')
    expect(headers.has('Content-Type')).toBe(false)
    expect(fetcher.mock.calls[0][1]?.body).toBe(form)
    expect(saved.etag).toBe('"new"')
  })

  it('saves only ordered association fields and does not retry stale gallery edits', async () => {
    const { artworkClient, fetcher } = await setup()
    fetcher.mockResolvedValueOnce(Response.json({ code: 'stale_version' }, { status: 412 }))
    const items = [{ id: 'entry', assetId: 'asset', role: 'logo', caption: '', isPreferred: true, usageRights: 'not editable' }]
    await expect(artworkClient.save('one', '"original"', items)).rejects.toMatchObject({ status: 412 })
    expect(fetcher).toHaveBeenCalledTimes(1)
    expect(JSON.parse(String(fetcher.mock.calls[0][1]?.body))).toEqual({
      items: [{ id: 'entry', assetId: 'asset', role: 'logo', caption: null, isPreferred: true }],
    })
  })

  it('loads images through bearer-authenticated requests, never credentials in URLs', async () => {
    const { artworkClient, authProvider, fetcher } = await setup()
    fetcher.mockResolvedValueOnce(new Response(new Blob(['image'], { type: 'image/jpeg' })))
    const blob = await artworkClient.image('asset-id')
    expect(blob.type).toBe('image/jpeg')
    expect(fetcher.mock.calls[0][0]).toBe('/api/v1/artwork-assets/asset-id/thumbnail')
    expect(new Headers(fetcher.mock.calls[0][1]?.headers).get('Authorization')).toBe('Bearer test-key')
    expect(fetcher.mock.calls[0][1]?.cache).toBe('no-store')
    await authProvider.logout({})
    await expect(artworkClient.image('asset-id')).rejects.toMatchObject({ status: 401 })
    expect(fetcher).toHaveBeenCalledTimes(1)
  })
})
