import { describe, expect, it, vi } from 'vitest'
import { beatportIdentities, createCatalogClient } from './catalog'

describe('Beatport entity identities', () => {
  it('prefers primary references, removes duplicate IDs and does not mutate catalog references', () => {
    const references = [
      { source: 'beatport', externalId: '42', isPrimary: false },
      { source: 'beatport', externalId: '7', isPrimary: true },
      { source: 'beatport', externalId: '42', isPrimary: true },
    ]
    const original = references.map(ref => ({ ...ref }))
    expect(beatportIdentities(references)).toEqual([{ id: 7, isPrimary: true }, { id: 42, isPrimary: true }])
    expect(references).toEqual(original)
  })

  it('transports scoped Artist release searches with cancellation and without a Label filter', async () => {
    const fetcher = vi.fn<typeof fetch>().mockResolvedValue(Response.json({ items: [], total: 0, page: 1, pageSize: 25 }))
    const { authProvider, beatportClient } = createCatalogClient(fetcher)
    await authProvider.login({ apiKey: 'test-key' })
    fetcher.mockClear()
    const controller = new AbortController()
    await beatportClient.releases({ artistId: 1001, q: 'A & B', sort: 'oldest', page: 1, pageSize: 25 }, controller.signal)
    expect(fetcher.mock.calls[0][0]).toBe('/api/v1/beatport/releases?artistId=1001&q=A+%26+B&sort=oldest&page=1&pageSize=25')
    expect(fetcher.mock.calls[0][1]?.signal).toBe(controller.signal)
    expect(fetcher.mock.calls[0][1]?.method).toBeUndefined()
  })

  it('only includes exact positive numeric Beatport identities that are safe to send as numbers', () => {
    expect(beatportIdentities([
      { source: 'discogs', externalId: '1', isPrimary: true },
      ...['0', '-2', '01', '1.5', '1e3', ' 42', 'verified-third-test-id', '9007199254740992'].map(externalId => ({
        source: 'beatport', externalId, isPrimary: false,
      })),
      { source: 'beatport', externalId: '9007199254740991', isPrimary: false },
    ])).toEqual([{ id: 9007199254740991, isPrimary: false }])
    expect(beatportIdentities()).toEqual([])
  })
})
