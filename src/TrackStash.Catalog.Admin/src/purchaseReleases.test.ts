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

  it('lists ready releases and sends batch admissions as dry runs or confirmed runs', async () => {
    const fetcher = vi.fn<typeof fetch>()
    const { authProvider, purchaseReleaseClient } = createCatalogClient(fetcher)
    fetcher.mockResolvedValueOnce(Response.json({ items: [] }))
    await authProvider.login({ apiKey: 'test-key' })
    fetcher.mockClear()
    fetcher.mockResolvedValueOnce(Response.json({ releaseIds: [10, 11] }))
    await expect(purchaseReleaseClient.readyIds()).resolves.toEqual([10, 11])
    expect(fetcher.mock.calls[0][0]).toBe('/api/v1/purchase-release-admissions/ready')
    fetcher.mockResolvedValueOnce(Response.json({ dryRun: true, items: [] }))
    await purchaseReleaseClient.admit([10, 11], true)
    expect(fetcher.mock.calls[1][0]).toBe('/api/v1/purchase-release-admissions')
    expect(fetcher.mock.calls[1][1]?.method).toBe('POST')
    expect(JSON.parse(fetcher.mock.calls[1][1]?.body as string)).toEqual({ releaseIds: [10, 11], dryRun: true, confirmed: false })
    fetcher.mockResolvedValueOnce(Response.json({ dryRun: false, items: [] }))
    await purchaseReleaseClient.admit([10], false)
    expect(JSON.parse(fetcher.mock.calls[2][1]?.body as string)).toEqual({ releaseIds: [10], dryRun: false, confirmed: true })
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
  it('sends Beatport sign-in, per-release backfill and bulk job requests to the catalog only', async () => {
    const fetcher = vi.fn<typeof fetch>()
    const { authProvider, beatportClient } = createCatalogClient(fetcher)
    fetcher.mockResolvedValueOnce(Response.json({ items: [] }))
    await authProvider.login({ apiKey: 'test-key' })
    fetcher.mockClear()
    fetcher.mockResolvedValueOnce(Response.json({ configured: true, reachable: true, signedIn: true }))
    await beatportClient.signIn('dj', 'secret')
    expect(fetcher.mock.calls[0][0]).toBe('/api/v1/beatport/sign-in')
    expect(fetcher.mock.calls[0][1]?.method).toBe('POST')
    expect(JSON.parse(fetcher.mock.calls[0][1]?.body as string)).toEqual({ username: 'dj', password: 'secret' })
    fetcher.mockResolvedValueOnce(Response.json({ releaseId: 10 }))
    await beatportClient.backfillRelease(10)
    expect(fetcher.mock.calls[1][0]).toBe('/api/v1/purchase-release-candidates/10/beatport-backfill')
    expect(fetcher.mock.calls[1][1]?.method).toBe('POST')
    fetcher.mockResolvedValueOnce(Response.json({ id: 'job 1', status: 'queued' }))
    await beatportClient.startJob()
    expect(fetcher.mock.calls[2][0]).toBe('/api/v1/beatport-backfill-jobs')
    fetcher.mockResolvedValueOnce(Response.json({ id: 'job 1', status: 'running' }))
    await beatportClient.job('job 1')
    expect(fetcher.mock.calls[3][0]).toBe('/api/v1/beatport-backfill-jobs/job%201')
    fetcher.mockResolvedValueOnce(Response.json({ detail: 'Sign in to Beatport first.', code: 'beatport_sign_in_required' }, { status: 409 }))
    await expect(beatportClient.backfillRelease(10)).rejects.toMatchObject({ status: 409, message: 'Sign in to Beatport first.' })
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
