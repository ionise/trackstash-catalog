import { HttpError } from 'react-admin'
import type { AuthProvider, DataProvider, GetOneParams, GetManyParams, RaRecord } from 'react-admin'

export interface LabelRecord {
  id: string
  name: string | null
  sortName: string | null
  version: string
  etag?: string
  profile?: LabelProfile
  externalReferences?: { source: string; externalId: string; isPrimary: boolean }[]
}

export interface LabelProfile {
  description: string | null
  countryCode: string | null
  city: string | null
  foundedYear: number | null
  lifecycleStatus: string
  notes: string | null
  favourite: boolean
  reviewStatus: string
  links: { kind: string; url: string; title: string | null; sourceUrl: string | null }[]
}

function profileForSave(profile: LabelProfile): LabelProfile {
  return {
    description: profile.description || null, countryCode: profile.countryCode || null,
    city: profile.city || null, foundedYear: profile.foundedYear ?? null,
    lifecycleStatus: profile.lifecycleStatus, notes: profile.notes || null,
    favourite: profile.favourite, reviewStatus: profile.reviewStatus,
    links: profile.links.map(link => ({
      kind: link.kind, url: link.url, title: link.title || null, sourceUrl: link.sourceUrl || null,
    })),
  }
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
  profile?: LabelProfile
  artwork?: ArtworkEntry[]
  relationships?: { id: string; fromLabelId: string; toLabelId: string; type: string; source: string; sourceUrl: string | null; notes: string | null }[]
  memberships?: GroupMembership[]
}
export interface RelatedLabel { id: string; name: string | null; version: string }
export interface LabelRelationship {
  id: string; from: RelatedLabel; to: RelatedLabel; type: 'imprint_of' | 'distributed_by'
  source: string; sourceUrl: string | null; notes: string | null; createdUtc: string
}
export interface LabelRelationships { labelId: string; items: LabelRelationship[]; etag: string }
export interface RelationshipInput {
  id: string | null; toLabelId: string; type: 'imprint_of' | 'distributed_by'; source: string
  sourceUrl: string | null; notes: string | null; targetVersion: string
}
export interface LabelGroup {
  id: string; kind: 'family' | 'organisation'; name: string; sortName: string | null
  description: string | null; website: string | null; version: string; currentMembers: number; etag?: string
}
export interface MembershipFields {
  status: 'current' | 'former' | 'unknown'; since: string | null; until: string | null
  source: string; sourceUrl: string | null; notes: string | null
}
export interface GroupMembership extends MembershipFields {
  id: string; label: RelatedLabel; group: LabelGroup; version: string; etag?: string
}
export interface GroupPage<T> { items: T[]; total: number; page: number; pageSize: number; counts?: Record<string, number> }
export interface RelatedGroupLabel { label: RelatedLabel; reasons: { kind: 'family' | 'organisation' | 'parent'; id: string; name: string }[] }

export interface ArtworkEntry {
  id: string
  assetId: string
  role: string
  caption: string | null
  isPreferred: boolean
}
export interface ArtworkItem extends ArtworkEntry {
  mediaType: string
  byteLength: number
  width: number | null
  height: number | null
  originalFilename: string | null
  sourceUrl: string | null
  attribution: string | null
  usageRights: string
}
export interface ArtworkGallery { labelId: string; items: ArtworkItem[]; etag: string }
export interface BackupInfo { id: string; filename: string; createdUtc: string; bytes: number }
export type BackupKind = 'catalog' | 'scanner'
export interface BackupSource { kind: BackupKind; name: string; configured: boolean }
export interface BackupList { items: BackupInfo[]; retainNewest: number }
export interface BackupJob {
  id: string; status: string; stage: string; createdUtc: string
  backup?: BackupInfo; warnings?: string[]; error?: string; kind?: BackupKind
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
      const message = response.status === 404 && (resource === 'backup-sources' || resource === 'backups' && (path === '' || path.startsWith('?')))
        ? 'The running catalog API does not provide backup support. Restart the API with the updated backend, then refresh backups.'
        : response.status === 412 && problem.code !== 'merge_state_changed' && problem.code !== 'relationship_state_changed' && problem.code !== 'group_state_changed'
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
      if (params.data.profile && JSON.stringify(profileForSave(params.data.profile)) !==
        JSON.stringify(params.previousData?.profile ? profileForSave(params.previousData.profile) : undefined))
        patch.profile = profileForSave(params.data.profile)
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
  async function gallery(response: Response): Promise<ArtworkGallery> {
    const etag = response.headers.get('ETag')
    if (!etag) throw new Error('Artwork response is missing its ETag; editing is unavailable.')
    return { ...await response.json(), etag }
  }
  const artworkClient = {
    get: async (id: string, signal?: AbortSignal) =>
      gallery(await request(`/${encodeURIComponent(id)}/artwork`, { signal })),
    upload: async (id: string, etag: string, form: FormData) =>
      gallery(await request(`/${encodeURIComponent(id)}/artwork`, {
        method: 'POST', headers: { 'If-Match': etag }, body: form,
      })),
    save: async (id: string, etag: string, items: ArtworkEntry[]) =>
      gallery(await request(`/${encodeURIComponent(id)}/artwork`, {
        method: 'PATCH', headers: { 'Content-Type': 'application/json', 'If-Match': etag },
        body: JSON.stringify({ items: items.map(({ id, assetId, role, caption, isPreferred }) =>
          ({ id, assetId, role, caption: caption || null, isPreferred })) }),
      })),
    image: async (assetId: string, original = false, signal?: AbortSignal) =>
      (await request(`/${encodeURIComponent(assetId)}/${original ? 'original' : 'thumbnail'}`, { signal }, 'artwork-assets')).blob(),
  }
  async function relationships(response: Response): Promise<LabelRelationships> {
    const etag = response.headers.get('ETag')
    if (!etag) throw new Error('Relationships response is missing its ETag; editing is unavailable.')
    return { ...await response.json(), etag }
  }
  const relationshipsClient = {
    get: async (id: string, signal?: AbortSignal) =>
      relationships(await request(`/${encodeURIComponent(id)}/relationships`, { signal })),
    save: async (id: string, etag: string, items: RelationshipInput[]) =>
      relationships(await request(`/${encodeURIComponent(id)}/relationships`, {
        method: 'PATCH', headers: { 'Content-Type': 'application/json', 'If-Match': etag }, body: JSON.stringify({ items }),
      })),
    edge: async (id: string, etag: string, item: {
      id: string | null; relatedLabelId: string; type: 'imprint_of' | 'distributed_by'; incoming: boolean
      source: string; sourceUrl: string | null; notes: string | null; targetVersion: string; remove: boolean
    }) => relationships(await request(`/${encodeURIComponent(id)}/relationship-edges`, {
      method: 'PATCH', headers: { 'Content-Type': 'application/json', 'If-Match': etag }, body: JSON.stringify(item),
    })),
  }
  async function tagged<T>(response: Response): Promise<T & { etag: string }> {
    const etag = response.headers.get('ETag')
    if (!etag) throw new Error('Catalog response is missing its edit token. Reload before editing.')
    return { ...await response.json(), etag }
  }
  const groupClient = {
    list: async (query: URLSearchParams, signal?: AbortSignal): Promise<GroupPage<LabelGroup>> =>
      (await request(`?${query}`, { signal }, 'label-groups')).json(),
    get: async (id: string, signal?: AbortSignal) =>
      tagged<LabelGroup>(await request(`/${encodeURIComponent(id)}`, { signal }, 'label-groups')),
    label: async (id: string, signal?: AbortSignal) => read<LabelRecord>(id, signal),
    create: async (fields: Pick<LabelGroup, 'kind' | 'name' | 'sortName' | 'description' | 'website'>) =>
      tagged<LabelGroup>(await request('', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(fields) }, 'label-groups')),
    update: async (id: string, etag: string, fields: Pick<LabelGroup, 'name' | 'sortName' | 'description' | 'website'>) =>
      tagged<LabelGroup>(await request(`/${encodeURIComponent(id)}`, {
        method: 'PATCH', headers: { 'Content-Type': 'application/merge-patch+json', 'If-Match': etag }, body: JSON.stringify(fields),
      }, 'label-groups')),
    members: async (labelId: string | undefined, groupId: string | undefined, page: number, status: string, signal?: AbortSignal): Promise<GroupPage<GroupMembership>> =>
      (await request(`/${encodeURIComponent(labelId ?? groupId ?? '')}/${labelId ? 'group-memberships' : 'members'}?page=${page}&pageSize=10&status=${status}`,
        { signal }, labelId ? 'labels' : 'label-groups')).json(),
    membership: async (id: string, signal?: AbortSignal) =>
      tagged<GroupMembership>(await request(`/${encodeURIComponent(id)}`, { signal }, 'label-group-memberships')),
    saveMembership: async (id: string | undefined, etag: string | undefined, fields: MembershipFields & {
      labelVersion: string; groupVersion: string; labelId?: string; groupId?: string; confirmRejoin: boolean
    }) => tagged<GroupMembership>(await request(id ? `/${encodeURIComponent(id)}` : '', {
      method: id ? 'PATCH' : 'POST',
      headers: { 'Content-Type': 'application/json', ...(etag ? { 'If-Match': etag } : {}) },
      body: JSON.stringify(fields),
    }, 'label-group-memberships')),
    removeMembership: async (id: string, etag: string) => {
      await request(`/${encodeURIComponent(id)}`, { method: 'DELETE', headers: { 'If-Match': etag } }, 'label-group-memberships')
    },
    related: async (id: string, page: number, kind: string, signal?: AbortSignal): Promise<GroupPage<RelatedGroupLabel>> =>
      (await request(`/${encodeURIComponent(id)}/related-labels?page=${page}&pageSize=10${kind ? `&kind=${kind}` : ''}`, { signal })).json(),
  }
  const backupsClient = {
    sources: async (signal?: AbortSignal): Promise<BackupSource[]> => (await request('', { signal }, 'backup-sources')).json(),
    list: async (signal?: AbortSignal, kind: BackupKind = 'catalog'): Promise<BackupList> =>
      (await request(kind === 'catalog' ? '' : '?kind=scanner', { signal }, 'backups')).json(),
    start: async (kind: BackupKind = 'catalog'): Promise<BackupJob> =>
      (await request(kind === 'catalog' ? '' : '?kind=scanner', { method: 'POST' }, 'backup-jobs')).json(),
    job: async (id: string, signal?: AbortSignal): Promise<BackupJob> =>
      (await request(`/${encodeURIComponent(id)}`, { signal }, 'backup-jobs')).json(),
    download: async (id: string, signal?: AbortSignal, kind: BackupKind = 'catalog') =>
      request(`/${encodeURIComponent(id)}/download${kind === 'scanner' ? '?kind=scanner' : ''}`, { signal }, 'backups'),
  }
  return { dataProvider, authProvider, mergeClient, artworkClient, backupsClient, relationshipsClient, groupClient }
}

export const { dataProvider, authProvider, mergeClient, artworkClient, backupsClient, relationshipsClient, groupClient } = createCatalogClient()
