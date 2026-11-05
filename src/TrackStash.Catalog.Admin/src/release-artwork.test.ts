import { describe, expect, it, vi } from 'vitest'
import { createCatalogClient } from './catalog'

describe('release artwork transport', () => {
  it('preserves original tokens on multipart upload and candidate attach without arbitrary URLs', async () => {
    const requests: { url: string; init?: RequestInit }[] = []
    const fetcher = vi.fn<typeof fetch>(async (url, init) => {
      requests.push({ url: String(url), init })
      return new Response(JSON.stringify({ releaseId: 'release/a', items: [] }), { status: 201, headers: { ETag: '"new"' } })
    })
    const client = createCatalogClient(fetcher)
    await client.authProvider.login({ apiKey: 'test-key' })
    requests.length = 0; fetcher.mockClear()
    const form = new FormData(); form.append('role', 'cover')
    expect((await client.releaseArtworkClient.upload('release/a', '"original"', form)).etag).toBe('"new"')
    expect(requests[0].url).toContain('/releases/release%2Fa/artwork')
    expect(requests[0].init?.body).toBe(form)
    expect(new Headers(requests[0].init?.headers).get('If-Match')).toBe('"original"')
    await client.releaseArtworkClient.attach('release/a', '"original"', {
      provider: 'provider', candidateId: 'opaque-id', revision: 'r1', title: 'Cover', sourceUrl: 'https://example.test/cover', attribution: 'Owner',
    }, 'Explicit permission', true)
    expect(JSON.parse(String(requests[1].init?.body))).toEqual({ provider: 'provider', candidateId: 'opaque-id', revision: 'r1', usageRights: 'Explicit permission', confirmed: true })
    expect(requests[1].url).toContain('/releases/release%2Fa/artwork-candidates/attach')
    expect(new Headers(requests[1].init?.headers).get('If-Match')).toBe('"original"')
    expect(fetcher).toHaveBeenCalledTimes(2)
  })
})
