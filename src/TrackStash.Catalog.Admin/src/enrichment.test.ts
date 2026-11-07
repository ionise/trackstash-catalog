import { expect, it, vi } from 'vitest'
import { createCatalogClient } from './catalog'

it('only sends an explicit authenticated lookup action for the Recording ID and forwards cancellation', async () => {
  const fetcher = vi.fn<typeof fetch>().mockResolvedValue(Response.json({ items: [] }))
  const { authProvider, trackClient } = createCatalogClient(fetcher)
  await authProvider.login({ apiKey: 'test-catalog-key' })
  fetcher.mockClear()
  const controller = new AbortController()
  await trackClient.enrichment('recording /1', controller.signal)
  expect(fetcher).toHaveBeenCalledTimes(1)
  expect(fetcher.mock.calls[0][0]).toBe('/api/v1/recordings/recording%20%2F1/enrichment/sonovault')
  expect(fetcher.mock.calls[0][1]?.method).toBe('POST')
  expect(fetcher.mock.calls[0][1]?.signal).toBe(controller.signal)
  expect(fetcher.mock.calls[0][1]?.body).toBeUndefined()
  expect(new Headers(fetcher.mock.calls[0][1]?.headers).has('x-api-key')).toBe(false)
})
