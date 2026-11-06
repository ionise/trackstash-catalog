import { describe, expect, it, vi } from 'vitest'
import { createCatalogClient } from './catalog'
import { defaultTrackSort, formatDuration, trackQuery } from './trackView'

describe('shared track list', () => {
  it('builds one query shape for every context, splitting sort and direction and omitting blanks', () => {
    expect(trackQuery({ releaseId: 'r/1', labelId: undefined }, { page: 2, q: '  ', sort: 'order' }).toString())
      .toBe('page=2&pageSize=25&releaseId=r%2F1&sort=order')
    expect(trackQuery({}, { page: 1, q: ' deep house ', sort: 'date_asc' }, true).toString())
      .toBe('page=1&pageSize=25&q=deep+house&sort=date&order=asc&groupBy=recording')
    expect(trackQuery({ artistId: 'a' }, { page: 1, q: '', sort: 'title' }).toString()).toBe('page=1&pageSize=25&artistId=a&sort=title')
  })
  it('defaults releases to release order and everything else to newest first', () => {
    expect(defaultTrackSort('release')).toBe('order')
    expect(['all', 'label', 'artist', 'recording'].map(c => defaultTrackSort(c as never))).toEqual(['date_desc', 'date_desc', 'date_desc', 'date_desc'])
  })
  it('formats lengths without inventing unknown values', () => {
    expect(formatDuration(null)).toBe('—')
    expect(formatDuration(425_999)).toBe('7:05')
  })
  it('reads tracks and Recordings with cancellation and encoded identifiers', async () => {
    const fetcher = vi.fn<typeof fetch>()
    const { authProvider, trackClient } = createCatalogClient(fetcher)
    fetcher.mockResolvedValueOnce(Response.json({ items: [] }))
    await authProvider.login({ apiKey: 'test-key' })
    const signal = new AbortController().signal
    fetcher.mockResolvedValueOnce(Response.json({ items: [], total: 0, page: 1, pageSize: 25 }))
    await trackClient.list(new URLSearchParams({ labelId: 'label & one', q: 'A & B' }), signal)
    expect(fetcher.mock.calls[1][0]).toBe('/api/v1/tracks?labelId=label+%26+one&q=A+%26+B')
    expect(fetcher.mock.calls[1][1]).toMatchObject({ signal, cache: 'no-store', credentials: 'omit' })
    fetcher.mockResolvedValueOnce(Response.json({ id: 'recording/one' }))
    await trackClient.recording('recording/one', signal)
    expect(fetcher.mock.calls[2][0]).toBe('/api/v1/recordings/recording%2Fone')
    fetcher.mockResolvedValueOnce(Response.json({ detail: 'Catalog Recording was not found.', code: 'recording_not_found' }, { status: 404 }))
    await expect(trackClient.recording('missing')).rejects.toMatchObject({ status: 404, message: 'Catalog Recording was not found.' })
  })
})
