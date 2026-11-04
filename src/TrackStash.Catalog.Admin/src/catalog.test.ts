import { describe, expect, it, vi } from 'vitest'
import { createCatalogClient } from './catalog'

function setup() {
  const fetcher = vi.fn<typeof fetch>()
  const client = createCatalogClient(fetcher)
  async function login() {
    fetcher.mockResolvedValueOnce(Response.json({ items: [], total: 0 }))
    await client.authProvider.login({ apiKey: 'test-key' })
    fetcher.mockClear()
  }
  return { ...client, fetcher, login }
}

describe('catalog data provider', () => {
  it('maps paging, search, filters, and sort to the public API', async () => {
    const { dataProvider, fetcher, login } = setup()
    await login()
    fetcher.mockResolvedValueOnce(Response.json({ items: [{ id: 'one' }], total: 77, page: 2, pageSize: 25 }))
    const result = await dataProvider.getList('labels', {
      pagination: { page: 2, perPage: 25 },
      sort: { field: 'sortName', order: 'DESC' },
      filter: { q: 'Café', source: 'beatport', externalId: '42', ignored: 'not sent' },
    })
    expect(result).toEqual({ data: [{ id: 'one' }], total: 77 })
    const url = new URL(String(fetcher.mock.calls[0][0]), 'http://localhost')
    expect(Object.fromEntries(url.searchParams)).toEqual({
      page: '2', pageSize: '25', sort: 'sortName', order: 'desc', q: 'Café', source: 'beatport', externalId: '42',
    })
    const headers = new Headers(fetcher.mock.calls[0][1]?.headers)
    expect(headers.get('Authorization')).toBe('Bearer test-key')
    expect(fetcher.mock.calls[0][1]?.credentials).toBe('omit')
  })

  it('retains the original read ETag, patches only editable changes, and clears sortName', async () => {
    const { dataProvider, fetcher, login } = setup()
    await login()
    fetcher.mockResolvedValueOnce(Response.json({ id: 'legacy/id', name: 'Original', sortName: 'Sort', version: 'v1' },
      { headers: { ETag: '"original"' } }))
    const { data: original } = await dataProvider.getOne('labels', { id: 'legacy/id' })
    expect(original.etag).toBe('"original"')
    expect(fetcher.mock.calls[0][0]).toBe('/api/v1/labels/legacy%2Fid')
    fetcher.mockResolvedValueOnce(Response.json({ ...original, sortName: null, version: 'v2' }, { headers: { ETag: '"new"' } }))
    const updated = await dataProvider.update('labels', {
      id: original.id, previousData: original,
      data: { ...original, sortName: '', sourcePayloadJson: 'must not be sent' },
    })
    const request = fetcher.mock.calls[1][1]!
    expect(new Headers(request.headers).get('If-Match')).toBe('"original"')
    expect(new Headers(request.headers).get('Content-Type')).toBe('application/merge-patch+json')
    expect(JSON.parse(String(request.body))).toEqual({ sortName: null })
    expect(updated.data.etag).toBe('"new"')
  })

  it('does not silently obtain a newer token to bypass a stale edit', async () => {
    const { dataProvider, fetcher, login } = setup()
    await login()
    fetcher.mockResolvedValueOnce(Response.json({ title: 'Changed', code: 'stale_version', traceId: 'trace' }, { status: 412 }))
    await expect(dataProvider.update('labels', {
      id: 'one', data: { name: 'Edit' }, previousData: { id: 'one', name: 'Original', etag: '"old"' },
    })).rejects.toMatchObject({ status: 412, message: expect.stringContaining('Your edit was not saved') })
    expect(fetcher).toHaveBeenCalledTimes(1)
  })

  it('fails explicitly without an edit token and never exposes delete/bulk operations', async () => {
    const { dataProvider, fetcher, login } = setup()
    await login()
    await expect(dataProvider.update('labels', { id: 'one', data: { name: 'Edit' }, previousData: { id: 'one' } }))
      .rejects.toMatchObject({ status: 428 })
    await expect(dataProvider.delete('labels', { id: 'one' })).rejects.toThrow('not available')
    await expect(dataProvider.getList('artists', { filter: {} })).rejects.toThrow('Unsupported')
    expect(fetcher).not.toHaveBeenCalled()
  })

  it('creates only editable fields without retrying POST', async () => {
    const { dataProvider, fetcher, login } = setup()
    await login()
    fetcher.mockResolvedValueOnce(Response.json({ title: 'Unavailable' }, { status: 500 }))
    await expect(dataProvider.create('labels', { data: { name: 'New', sortName: '', id: 'caller-id' } }))
      .rejects.toMatchObject({ status: 500 })
    expect(fetcher).toHaveBeenCalledTimes(1)
    expect(JSON.parse(String(fetcher.mock.calls[0][1]?.body))).toEqual({ name: 'New', sortName: null })
  })

  it('maps field errors and forwards read cancellation', async () => {
    const { dataProvider, fetcher, login } = setup()
    await login()
    fetcher.mockResolvedValueOnce(Response.json({ title: 'Invalid', errors: { name: ['Cannot be blank'] } }, { status: 400 }))
    await expect(dataProvider.create('labels', { data: { name: ' ' } }))
      .rejects.toMatchObject({ body: { errors: { name: 'Cannot be blank' } } })
    fetcher.mockResolvedValueOnce(Response.json({ id: 'one' }, { headers: { ETag: '"one"' } }))
    const controller = new AbortController()
    await dataProvider.getOne('labels', { id: 'one', signal: controller.signal })
    expect(fetcher.mock.calls[1][1]?.signal).toBe(controller.signal)
  })
})

describe('memory-only authentication', () => {
  it('validates keys before login and clears them after failed login, logout, and 401', async () => {
    const { authProvider, dataProvider, fetcher, login } = setup()
    fetcher.mockResolvedValueOnce(Response.json({ title: 'Wrong key' }, { status: 401 }))
    await expect(authProvider.login({ apiKey: 'wrong' })).rejects.toMatchObject({ status: 401 })
    await expect(authProvider.checkAuth({})).rejects.toThrow()
    await login()
    await authProvider.checkAuth({})
    await authProvider.logout({})
    await expect(dataProvider.getOne('labels', { id: 'one' })).rejects.toMatchObject({ status: 401 })
    await login()
    await expect(authProvider.checkError({ status: 401 })).rejects.toThrow()
    await expect(authProvider.checkAuth({})).rejects.toThrow()
  })

  it('does not log out on conflicts or origin/server failures', async () => {
    const { authProvider, login } = setup()
    await login()
    for (const status of [403, 412, 500]) await authProvider.checkError({ status })
    await authProvider.checkAuth({})
  })
})
