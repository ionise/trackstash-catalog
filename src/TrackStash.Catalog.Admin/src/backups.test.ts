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
describe('backup client', () => {
  it('accepts an empty backup list without an error', async () => {
    const { backupsClient, fetcher } = await setup()
    fetcher.mockResolvedValueOnce(Response.json({ items: [], retainNewest: 10 }))
    await expect(backupsClient.list()).resolves.toEqual({ items: [], retainNewest: 10 })
  })
  it('explains that an older running backend needs restarting when the list route is missing', async () => {
    const { backupsClient, fetcher } = await setup()
    fetcher.mockResolvedValueOnce(Response.json({ title: 'Request could not be completed', code: 'http_error' }, { status: 404 }))
    await expect(backupsClient.list()).rejects.toMatchObject({ status: 404, message: expect.stringContaining('Restart the API') })
  })
  it('starts jobs without a browser-supplied path and reads status with bearer auth', async () => {
    const { backupsClient, fetcher } = await setup()
    fetcher.mockResolvedValueOnce(Response.json({ id: 'one', status: 'queued' }, { status: 202 }))
    await backupsClient.start()
    expect(fetcher.mock.calls[0][0]).toBe('/api/v1/backup-jobs')
    expect(fetcher.mock.calls[0][1]?.method).toBe('POST')
    expect(fetcher.mock.calls[0][1]?.body).toBeUndefined()
    expect(new Headers(fetcher.mock.calls[0][1]?.headers).get('Authorization')).toBe('Bearer test-key')
    fetcher.mockResolvedValueOnce(Response.json({ id: 'one', status: 'completed' }))
    await backupsClient.job('one')
    expect(fetcher.mock.calls[1][0]).toBe('/api/v1/backup-jobs/one')
  })
  it('exposes a protected download stream without putting credentials in URLs or loading a blob', async () => {
    const { backupsClient, fetcher, authProvider } = await setup()
    fetcher.mockResolvedValueOnce(new Response('zip', { headers: { 'Content-Type': 'application/zip' } }))
    const response = await backupsClient.download('backup/id')
    expect(await response.text()).toBe('zip')
    expect(fetcher.mock.calls[0][0]).toBe('/api/v1/backups/backup%2Fid/download')
    expect(new Headers(fetcher.mock.calls[0][1]?.headers).get('Authorization')).toBe('Bearer test-key')
    expect(fetcher.mock.calls[0][1]?.credentials).toBe('omit')
    await authProvider.logout({})
    await expect(backupsClient.list()).rejects.toMatchObject({ status: 401 })
  })
  it('surfaces busy errors without retrying jobs', async () => {
    const { backupsClient, fetcher } = await setup()
    fetcher.mockResolvedValueOnce(Response.json({ code: 'backup_busy', detail: 'Wait for download.' }, { status: 409 }))
    await expect(backupsClient.start()).rejects.toMatchObject({ status: 409, message: 'Wait for download.' })
    expect(fetcher).toHaveBeenCalledTimes(1)
  })
})
