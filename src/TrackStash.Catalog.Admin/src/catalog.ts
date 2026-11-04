import { HttpError } from 'react-admin'
import type { AuthProvider, DataProvider, GetOneParams, GetManyParams, RaRecord } from 'react-admin'

export interface LabelRecord {
  id: string
  name: string | null
  sortName: string | null
  version: string
  etag?: string
}

interface Problem {
  title?: string
  detail?: string
  code?: string
  traceId?: string
  errors?: Record<string, string[]>
}

export interface MergePreview {
  operationId: string
  expiresUtc: string
  survivor: LabelRecord
  retired: LabelRecord
  aliases: { value: string; isPrimary: boolean }[]
  externalReferences: { source: string; externalId: string; isPrimary: boolean }[]
  releaseLinks: { releaseId: string; isPrimary: boolean; role: string | null }[]
  blockers: string[]
  warnings: string[]
  etag: string
}

export function createCatalogClient(fetcher: typeof fetch = fetch) {
  let token: string | undefined

  async function request(path: string, init: RequestInit = {}, resource = 'labels') {
    if (!token) throw new HttpError('Sign in with your catalog API key.', 401)
    const headers = new Headers(init.headers)
    headers.set('Authorization', `Bearer ${token}`)
    const response = await fetcher(`/api/v1/${resource}${path}`, {
      ...init, headers, cache: 'no-store', credentials: 'omit',
    })
    if (!response.ok) {
      const problem: Problem = await response.json()
      const message = response.status === 412 && problem.code !== 'merge_state_changed'
        ? 'This label changed since you opened it. Your edit was not saved. Reload the label before editing again.'
        : problem.detail ?? problem.title ?? `Catalog request failed (${response.status}).`
      throw new HttpError(message, response.status, {
        ...problem,
        errors: problem.errors
          ? Object.fromEntries(Object.entries(problem.errors).map(([field, messages]) => [field, messages.join(' ')]))
          : undefined,
      })
    }
    return response
  }

  function labelResource(resource: string) {
    if (resource !== 'labels') throw new Error(`Unsupported catalog resource: ${resource}`)
  }

  async function read<RecordType extends RaRecord>(id: string | number, signal?: AbortSignal) {
    const response = await request(`/${encodeURIComponent(id)}`, { signal })
    const label: RecordType = await response.json()
    const etag = response.headers.get('ETag')
    if (!etag) throw new Error('Catalog response is missing an ETag; editing is unavailable.')
    return { ...label, etag }
  }

  const unsupported = async () => { throw new Error('This catalog operation is not available yet.') }

  const dataProvider: DataProvider = {
    getList: async (resource, params) => {
      labelResource(resource)
      const query = new URLSearchParams({
        page: String(params.pagination?.page ?? 1),
        pageSize: String(params.pagination?.perPage ?? 25),
        sort: params.sort?.field ?? 'name',
        order: (params.sort?.order ?? 'ASC').toLowerCase(),
      })
      for (const field of ['q', 'normalizedName', 'source', 'externalId']) {
        const value: unknown = params.filter?.[field]
        if (typeof value === 'string' && value.trim()) query.set(field, value)
      }
      const response = await request(`?${query}`, { signal: params.signal })
      const page = await response.json()
      return { data: page.items, total: page.total }
    },
    getOne: async <RecordType extends RaRecord>(resource: string, params: GetOneParams<RecordType> & { signal?: AbortSignal }) => {
      labelResource(resource)
      return { data: await read<RecordType>(params.id, params.signal) }
    },
    getMany: async <RecordType extends RaRecord>(resource: string, params: GetManyParams<RecordType> & { signal?: AbortSignal }) => {
      labelResource(resource)
      return { data: await Promise.all(params.ids.map(id => read<RecordType>(id, params.signal))) }
    },
    create: async (resource, params) => {
      labelResource(resource)
      const response = await request('', {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ name: params.data.name, sortName: params.data.sortName || null }),
      })
      return { data: { ...await response.json(), etag: response.headers.get('ETag') } }
    },
    update: async (resource, params) => {
      labelResource(resource)
      // Use the form's original read token, never a freshly fetched token or shared last-seen cache.
      const etag: unknown = params.previousData?.etag
      if (typeof etag !== 'string' || !etag)
        throw new HttpError('Reload this label to obtain its edit version.', 428)
      const patch: Record<string, unknown> = {}
      for (const field of ['name', 'sortName'] as const) {
        if (params.data[field] !== params.previousData?.[field])
          patch[field] = field === 'sortName' ? params.data[field] || null : params.data[field]
      }
      if (!Object.keys(patch).length) return { data: params.previousData }
      const response = await request(`/${encodeURIComponent(params.id)}`, {
        method: 'PATCH',
        headers: { 'Content-Type': 'application/merge-patch+json', 'If-Match': etag },
        body: JSON.stringify(patch),
      })
      return { data: { ...await response.json(), etag: response.headers.get('ETag') } }
    },
    getManyReference: unsupported,
    updateMany: unsupported,
    delete: unsupported,
    deleteMany: unsupported,
    supportAbortSignal: true,
  }

  const authProvider: AuthProvider = {
    login: async ({ apiKey }: { apiKey: string }) => {
      token = apiKey
      try {
        await request('?pageSize=1')
      } catch (error) {
        token = undefined
        throw error
      }
    },
    logout: async () => { token = undefined },
    checkAuth: async () => {
      if (!token) throw new Error('Sign in to the local catalog.')
    },
    checkError: async (error: { status?: number }) => {
      if (error.status === 401) {
        token = undefined
        throw new Error('Your API key is no longer accepted. Sign in again.')
      }
    },
    getPermissions: async () => [],
  }

  const mergeClient = {
    preview: async (survivorId: string, retiredLabelId: string): Promise<MergePreview> => {
      const response = await request(`/${encodeURIComponent(survivorId)}/merge-previews`, {
        method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ retiredLabelId }),
      })
      const etag = response.headers.get('ETag')
      if (!etag) throw new Error('Merge review is missing its ETag; apply is unavailable.')
      return { ...await response.json(), etag }
    },
    apply: async (preview: MergePreview, reason: string, operator: string) => {
      const response = await request(`/${encodeURIComponent(preview.operationId)}/apply`, {
        method: 'POST', headers: { 'Content-Type': 'application/json', 'If-Match': preview.etag },
        body: JSON.stringify({ reason, operator: operator || null }),
      }, 'label-merge-previews')
      return await response.json() as { canonicalId: string; operationId: string; auditId: string }
    },
  }
  return { dataProvider, authProvider, mergeClient }
}

export const { dataProvider, authProvider, mergeClient } = createCatalogClient()
