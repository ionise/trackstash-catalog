import { describe, expect, it, vi } from 'vitest'
import { bulkImportReason, createCatalogClient, straightforwardImportDecisions } from './catalog'
import type { ImportIdentity } from './catalog'

describe('straightforward bulk identity acceptance', () => {
  it('only accepts unambiguous undecided new identities and records deliberate bulk provenance', () => {
    const identity = (providerId: number, fields: Partial<ImportIdentity> = {}): ImportIdentity => ({
      kind: 'artist', providerId, name: 'Act', action: 'undecided', canonicalId: null, compoundReview: false,
      mixName: null, isrc: null, evidence: null, candidates: [], ...fields,
    })
    const decisions = straightforwardImportDecisions([
      identity(1), identity(2, { compoundReview: true }),
      identity(3, { candidates: [{ id: 'existing', name: 'Act', mixName: null, isrc: null }] }),
      identity(4, { action: 'reuse' }), identity(5, { kind: 'release' }),
      identity(6, { kind: 'recording' }), identity(7, { kind: 'recording' }),
    ], ['Track 7: another source listing shares ISRC or title/version. Review master identity; distinct listings are not automatically merged.'])
    expect(decisions.map(item => item.providerId)).toEqual([1, 6])
    expect(decisions.every(item => item.action === 'create' && item.canonicalId === null && item.evidence === bulkImportReason)).toBe(true)
  })
})

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
    fetcher.mockResolvedValueOnce(Response.json({ items: [], total: 0, page: 1, pageSize: 25, admissionStatusAvailable: true, hideAdmitted: true }))
    await purchaseReleaseClient.list(1, '', abort.signal, true)
    expect(fetcher.mock.calls[3][0]).toBe('/api/v1/purchase-release-candidates?page=1&pageSize=25&q=&hideAdmitted=true')
    fetcher.mockResolvedValueOnce(Response.json({ items: [], total: 0, page: 1, pageSize: 25, admissionStatusAvailable: true, hideAdmitted: true, readyOnly: true }))
    await purchaseReleaseClient.list(1, '', abort.signal, true, true)
    expect(fetcher.mock.calls[4][0]).toBe('/api/v1/purchase-release-candidates?page=1&pageSize=25&q=&hideAdmitted=true&readyOnly=true')
  })

  describe('immutable release import transport', () => {
    it('keeps plans server-side, requires an original token and submits one evidenced decision at a time', async () => {
      const fetcher = vi.fn<typeof fetch>()
      const { authProvider, releaseImportClient } = createCatalogClient(fetcher)
      fetcher.mockResolvedValueOnce(Response.json({ items: [] }))
      await authProvider.login({ apiKey: 'test-key' })
      fetcher.mockResolvedValueOnce(Response.json({ operationId: 'new-plan' }, { headers: { ETag: '"original-token"' } }))
      const decision = { kind: 'artist' as const, providerId: 5, action: 'unresolved' as const, canonicalId: null, evidence: 'Compound identity requires later split review' }
      const preview = await releaseImportClient.preview(10, 'old-plan', decision)
      expect(preview.etag).toBe('"original-token"')
      expect(JSON.parse(fetcher.mock.calls[1][1]?.body as string)).toEqual({ releaseId: 10, basePreviewId: 'old-plan', decision })
      fetcher.mockResolvedValueOnce(Response.json({ operationId: 'new-plan', noOp: false }))
      await releaseImportClient.apply(preview, 'Reviewed release', true)
      expect(fetcher.mock.calls[2][0]).toBe('/api/v1/release-import-previews/new-plan/apply')
      expect(new Headers(fetcher.mock.calls[2][1]?.headers).get('If-Match')).toBe('"original-token"')
      expect(JSON.parse(fetcher.mock.calls[2][1]?.body as string)).toEqual({ reason: 'Reviewed release', confirmed: true })
    })
    it('does not accept missing ETags or automatically retry stale apply', async () => {
      const fetcher = vi.fn<typeof fetch>()
      const { authProvider, releaseImportClient } = createCatalogClient(fetcher)
      fetcher.mockResolvedValueOnce(Response.json({ items: [] }))
      await authProvider.login({ apiKey: 'test-key' })
      fetcher.mockResolvedValueOnce(Response.json({ operationId: 'unsafe-plan' }))
      await expect(releaseImportClient.preview(10)).rejects.toThrow('missing its edit token')
      fetcher.mockResolvedValueOnce(Response.json({ operationId: 'plan' }, { headers: { ETag: '"token"' } }))
      const preview = await releaseImportClient.preview(10)
      fetcher.mockClear()
      fetcher.mockResolvedValueOnce(Response.json({ detail: 'Catalog changed', code: 'import_state_changed' }, { status: 412 }))
      await expect(releaseImportClient.apply(preview, 'Reviewed release', true)).rejects.toMatchObject({ status: 412 })
      expect(fetcher).toHaveBeenCalledTimes(1)
    })
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
