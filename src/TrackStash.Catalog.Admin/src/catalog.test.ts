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
    expect(fetcher.mock.calls[0][1]?.credentials).toBe('same-origin')
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
  it('accepts a trusted proxy session and redirects sign-out to the gateway', async () => {
    const { authProvider, dataProvider, fetcher } = setup()
    vi.stubGlobal('window', { location: { origin: 'https://catalog.example.test' } })
    try {
      fetcher.mockResolvedValueOnce(Response.json({
        available: false, authenticated: true, proxyUser: 'approved-user',
        proxyLogoutPath: '/outpost.goauthentik.io/sign_out',
      }))
      await expect(authProvider.checkAuth({})).resolves.toBeUndefined()
      fetcher.mockResolvedValueOnce(Response.json({ id: 'one' }, { headers: { ETag: '"one"' } }))
      await dataProvider.getOne('labels', { id: 'one' })
      expect(new Headers(fetcher.mock.calls[1][1]?.headers).get('Authorization')).toBeNull()
      fetcher.mockResolvedValueOnce(new Response(null, { status: 204 }))
      await expect(authProvider.logout({})).resolves.toBe('https://catalog.example.test/outpost.goauthentik.io/sign_out')
    } finally {
      vi.unstubAllGlobals()
    }
  })

  it('rejects invalid gateway logout redirects in session responses', async () => {
    const { authProvider, fetcher } = setup()
    fetcher.mockResolvedValueOnce(Response.json({
      available: false, authenticated: true, proxyUser: 'approved-user', proxyLogoutPath: '//evil.example/logout',
    }))
    await expect(authProvider.checkAuth({})).rejects.toThrow('invalid proxy session status')
  })

  it('uses the same-origin session cookie and restores it after a page refresh', async () => {
    const { authProvider, dataProvider, fetcher, sessionClient } = setup()
    fetcher.mockResolvedValueOnce(Response.json({ available: true, authenticated: false }))
    await expect(sessionClient.status()).resolves.toEqual({ available: true, authenticated: false })
    fetcher.mockResolvedValueOnce(new Response(null, { status: 204 }))
    await authProvider.login({ username: 'admin', password: 'secret' })
    expect(fetcher.mock.calls[1][0]).toBe('/api/v1/session')
    expect(fetcher.mock.calls[1][1]).toMatchObject({
      method: 'POST', credentials: 'same-origin', cache: 'no-store',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ username: 'admin', password: 'secret' }),
    })
    expect(new Headers(fetcher.mock.calls[1][1]?.headers).get('Authorization')).toBeNull()

    fetcher.mockResolvedValueOnce(Response.json({ available: true, authenticated: true }))
    await authProvider.checkAuth({})
    fetcher.mockResolvedValueOnce(Response.json({ id: 'one' }, { headers: { ETag: '"one"' } }))
    await dataProvider.getOne('labels', { id: 'one' })
    expect(new Headers(fetcher.mock.calls[3][1]?.headers).get('Authorization')).toBeNull()
    expect(fetcher.mock.calls[3][1]?.credentials).toBe('same-origin')

    fetcher.mockResolvedValueOnce(new Response(null, { status: 204 }))
    await authProvider.logout({})
    expect(fetcher.mock.calls[4][0]).toBe('/api/v1/session')
    expect(fetcher.mock.calls[4][1]).toMatchObject({ method: 'DELETE', credentials: 'same-origin' })
  })

  it('falls back to the existing bearer-key flow when an older API has no session endpoint', async () => {
    const { authProvider, dataProvider, fetcher, sessionClient } = setup()
    fetcher.mockResolvedValueOnce(new Response(null, { status: 404 }))
    await expect(sessionClient.status()).resolves.toEqual({ available: false, authenticated: false })
    fetcher.mockResolvedValueOnce(Response.json({ items: [] }))
    await authProvider.login({ apiKey: 'test-key' })
    fetcher.mockResolvedValueOnce(Response.json({ items: [], total: 0 }))
    await dataProvider.getList('labels', { pagination: { page: 1, perPage: 25 }, sort: { field: 'name', order: 'ASC' }, filter: {} })
    expect(new Headers(fetcher.mock.calls[2][1]?.headers).get('Authorization')).toBe('Bearer test-key')
    expect(fetcher.mock.calls[2][1]?.credentials).toBe('same-origin')
  })

  it('revokes a native cookie acquired in another tab when signing out with an API key', async () => {
    let cookieActive = false
    const fetcher = vi.fn<typeof fetch>(async (url, init) => {
      if (url === '/api/v1/session') {
        if (init?.method === 'POST') {
          cookieActive = true
          return new Response(null, { status: 204 })
        }
        if (init?.method === 'DELETE') {
          if (init.credentials === 'same-origin') cookieActive = false
          return new Response(null, { status: 204 })
        }
        return Response.json({ available: true, authenticated: cookieActive })
      }
      if (new Headers(init?.headers).get('Authorization') === 'Bearer test-key') return Response.json({ items: [] })
      return Response.json({ title: 'Wrong key' }, { status: 401 })
    })
    const { authProvider } = createCatalogClient(fetcher)
    await authProvider.login({ apiKey: 'test-key' })
    const otherTab = createCatalogClient(fetcher)
    await otherTab.authProvider.login({ username: 'admin', password: 'secret' })

    fetcher.mockRejectedValueOnce(new TypeError('Network unavailable'))
    await expect(authProvider.logout({})).rejects.toThrow('Could not confirm sign-out')
    await expect(authProvider.checkAuth({})).resolves.toBeUndefined()
    expect(cookieActive).toBe(true)

    await authProvider.logout({})
    expect(fetcher).toHaveBeenLastCalledWith('/api/v1/session', expect.objectContaining({ method: 'DELETE' }))
    await expect(createCatalogClient(fetcher).authProvider.checkAuth({})).rejects.toThrow('Sign in')
  })

  it('clears expired native sessions and requires a fresh sign-in', async () => {
    const { authProvider, dataProvider, fetcher } = setup()
    fetcher.mockResolvedValueOnce(new Response(null, { status: 204 }))
    await authProvider.login({ username: 'admin', password: 'secret' })
    fetcher.mockResolvedValueOnce(Response.json({ title: 'Unauthorized' }, { status: 401 }))
    await expect(dataProvider.getList('labels', { pagination: { page: 1, perPage: 25 }, sort: { field: 'name', order: 'ASC' }, filter: {} }))
      .rejects.toMatchObject({ status: 401 })
    await expect(authProvider.checkError({ status: 401 })).rejects.toThrow('Your session has expired.')
    fetcher.mockResolvedValueOnce(Response.json({ available: true, authenticated: false }))
    await expect(authProvider.checkAuth({})).rejects.toThrow()
  })

  it('validates keys before login and clears them after failed login, logout, and 401', async () => {
    const { authProvider, dataProvider, fetcher, login } = setup()
    fetcher.mockResolvedValueOnce(Response.json({ title: 'Wrong key' }, { status: 401 }))
    await expect(authProvider.login({ apiKey: 'wrong' })).rejects.toMatchObject({ status: 401 })
    await expect(authProvider.checkAuth({})).rejects.toThrow()
    await login()
    await authProvider.checkAuth({})
    fetcher.mockResolvedValueOnce(new Response(null, { status: 404 }))
    await authProvider.logout({})
    expect(new Headers(fetcher.mock.lastCall?.[1]?.headers).get('Authorization')).toBe('Bearer test-key')
    await expect(dataProvider.getOne('labels', { id: 'one' })).rejects.toMatchObject({ status: 401 })
    await login()
    await expect(authProvider.checkError({ status: 401 })).rejects.toThrow()
    await expect(authProvider.checkAuth({})).rejects.toThrow()
  })

  describe('reviewed merges', () => {
    it('uses the reviewed ETag and operation for apply without refetch or automatic retry', async () => {
      const { mergeClient, fetcher, login } = setup()
      await login()
      fetcher.mockResolvedValueOnce(Response.json({ operationId: 'review', blockers: [] }, { headers: { ETag: '"review-token"' } }))
      const preview = await mergeClient.preview('survivor/id', 'retired')
      expect(fetcher.mock.calls[0][0]).toBe('/api/v1/labels/survivor%2Fid/merge-previews')
      expect(JSON.parse(String(fetcher.mock.calls[0][1]?.body))).toEqual({ retiredLabelId: 'retired' })
      fetcher.mockResolvedValueOnce(Response.json({ title: 'Merge not applied', detail: 'Create a fresh preview.', code: 'merge_state_changed' }, { status: 412 }))
      await expect(mergeClient.apply(preview, 'Same label', '')).rejects.toMatchObject({ status: 412, message: 'Create a fresh preview.' })
      expect(fetcher).toHaveBeenCalledTimes(2)
      expect(fetcher.mock.calls[1][0]).toBe('/api/v1/label-merge-previews/review/apply')
      expect(new Headers(fetcher.mock.calls[1][1]?.headers).get('If-Match')).toBe('"review-token"')
      expect(JSON.parse(String(fetcher.mock.calls[1][1]?.body))).toEqual({ reason: 'Same label', operator: null })
    })

    describe('manual label profiles', () => {
      it('sends a complete changed profile with the original ETag and explicit empty values', async () => {
        const { dataProvider, fetcher, login } = setup()
        await login()
        const profile = {
          description: 'Original', countryCode: 'ES', city: null, foundedYear: 2007, lifecycleStatus: 'active',
          notes: null, favourite: true, reviewStatus: 'reviewed',
          links: [{ kind: 'official', url: 'https://example.test', title: 'Official', sourceUrl: null }],
        }
        fetcher.mockResolvedValueOnce(Response.json({ id: 'one', profile }, { headers: { ETag: '"new"' } }))
        await dataProvider.update('labels', {
          id: 'one', previousData: { id: 'one', profile, etag: '"original"' },
          data: { id: 'one', profile: { ...profile, description: '', favourite: false, links: [] } },
        })
        expect(new Headers(fetcher.mock.calls[0][1]?.headers).get('If-Match')).toBe('"original"')
        expect(JSON.parse(String(fetcher.mock.calls[0][1]?.body))).toEqual({
          profile: { ...profile, description: null, favourite: false, links: [] },
        })
      })

      it('does not send an unchanged profile during a name-only edit', async () => {
        const { dataProvider, fetcher, login } = setup()
        await login()
        const profile = { description: null, countryCode: null, city: null, foundedYear: null, lifecycleStatus: 'unknown',
          notes: null, favourite: false, reviewStatus: 'unreviewed', links: [] }
        fetcher.mockResolvedValueOnce(Response.json({ id: 'one', name: 'Updated', profile }, { headers: { ETag: '"new"' } }))
        await dataProvider.update('labels', {
          id: 'one', previousData: { id: 'one', name: 'Original', profile, etag: '"original"' },
          data: { id: 'one', name: 'Updated', profile: { ...profile } },
        })
        expect(JSON.parse(String(fetcher.mock.calls[0][1]?.body))).toEqual({ name: 'Updated' })
      })
    })

    it('does not allow a review without an ETag to be confirmed', async () => {
      const { mergeClient, fetcher, login } = setup()
      await login()
      fetcher.mockResolvedValueOnce(Response.json({ operationId: 'review' }))
      await expect(mergeClient.preview('one', 'two')).rejects.toThrow('missing its ETag')
    })
  })

  it('does not log out on conflicts or origin/server failures', async () => {
    const { authProvider, login } = setup()
    await login()
    for (const status of [403, 412, 500]) await authProvider.checkError({ status })
    await authProvider.checkAuth({})
  })
})
