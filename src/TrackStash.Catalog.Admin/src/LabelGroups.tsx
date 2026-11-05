import { useCallback, useEffect, useState } from 'react'
import { Alert, Box, Button, Checkbox, Dialog, DialogActions, DialogContent, DialogTitle, FormControlLabel, Link, MenuItem, Stack, TextField, Typography } from '@mui/material'
import { useAuthenticated, useDataProvider, useLogoutIfAccessDenied, useRecordContext } from 'react-admin'
import { Link as RouterLink, useNavigate, useParams } from 'react-router-dom'
import { groupClient } from './catalog'
import type { GroupMembership, GroupPage, LabelGroup, LabelRecord, MembershipFields, RelatedGroupLabel, RelatedLabel } from './catalog'

function failureMessage(error: unknown) { return error instanceof Error ? error.message : 'The catalog request failed.' }
function isStale(error: unknown) { return !!error && typeof error === 'object' && 'status' in error && error.status === 412 }
function Pager({ page, total, onPage, disabled = false }: { page: number; total: number; onPage: (page: number) => void; disabled?: boolean }) {
  return <Stack direction="row" sx={{ alignItems: 'center', flexWrap: 'wrap' }}>
    <Button disabled={disabled || page <= 1} onClick={() => onPage(page - 1)}>Previous page</Button>
    <Typography>Page {page}; {total} results</Typography>
    <Button disabled={disabled || page * 10 >= total} onClick={() => onPage(page + 1)}>Next page</Button>
  </Stack>
}

export function LabelGroupList() {
  const { isPending } = useAuthenticated()
  const logout = useLogoutIfAccessDenied()
  const [search, setSearch] = useState('')
  const [kind, setKind] = useState('')
  const [page, setPage] = useState(1)
  const [data, setData] = useState<GroupPage<LabelGroup>>()
  const [error, setError] = useState('')
  useEffect(() => {
    if (isPending) return
    const controller = new AbortController()
    const query = new URLSearchParams({ page: String(page), pageSize: '10' })
    if (search.trim()) query.set('q', search)
    if (kind) query.set('kind', kind)
    void groupClient.list(query, controller.signal).then(result => { setData(result); setError('') }).catch(failure => {
      if (!controller.signal.aborted) { setError(failureMessage(failure)); void logout(failure) }
    })
    return () => controller.abort()
  }, [page, search, kind, logout, isPending])
  return <Stack spacing={2} sx={{ p: 3 }}>
    <Typography variant="h4" component="h1">Label groups</Typography>
    <Typography>Families and organisations group distinct labels. Membership does not imply legal ownership or an imprint relationship.</Typography>
    <Button component={RouterLink} to="/label-groups/create">Create group</Button>
    <TextField label="Search groups" value={search} onChange={e => { setSearch(e.target.value); setPage(1) }} />
    <TextField select label="Group kind" value={kind} onChange={e => { setKind(e.target.value); setPage(1) }}>
      <MenuItem value="">All kinds</MenuItem><MenuItem value="family">Family / brand</MenuItem><MenuItem value="organisation">Organisation</MenuItem>
    </TextField>
    {error && <Alert severity="error">{error}</Alert>}
    {!error && data?.total === 0 && <Typography>No label groups recorded.</Typography>}
    {!error && data?.items.map(group => <Box key={group.id} sx={{ overflowWrap: 'anywhere' }}>
      <Link component={RouterLink} to={`/label-groups/${group.id}/show`}>{group.name}</Link>
      <Typography>{group.kind === 'family' ? 'Family / brand' : 'Organisation'}; {group.currentMembers} current members</Typography>
    </Box>)}
    {data && !error && <Pager page={page} total={data.total} onPage={setPage} />}
  </Stack>
}

export function LabelGroupCreate() { return <GroupForm /> }
export function LabelGroupEdit() { const { id } = useParams(); return <GroupForm key={id} id={id} /> }
function GroupForm({ id }: { id?: string }) {
  const { isPending } = useAuthenticated()
  const navigate = useNavigate()
  const logout = useLogoutIfAccessDenied()
  const [group, setGroup] = useState<LabelGroup>()
  const [kind, setKind] = useState<'family' | 'organisation'>('family')
  const [name, setName] = useState('')
  const [sortName, setSortName] = useState('')
  const [description, setDescription] = useState('')
  const [website, setWebsite] = useState('')
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)
  const [stale, setStale] = useState(false)
  async function load() {
    if (!id) return
    setBusy(true); setGroup(undefined); setError('')
    try {
      const current = await groupClient.get(id)
      setGroup(current); setKind(current.kind); setName(current.name); setSortName(current.sortName ?? '')
      setDescription(current.description ?? ''); setWebsite(current.website ?? ''); setStale(false)
    } catch (failure) { setError(failureMessage(failure)); await logout(failure) }
    finally { setBusy(false) }
  }
  useEffect(() => {
    if (!id || isPending) return
    const controller = new AbortController()
    void groupClient.get(id, controller.signal).then(current => {
      setGroup(current); setKind(current.kind); setName(current.name); setSortName(current.sortName ?? '')
      setDescription(current.description ?? ''); setWebsite(current.website ?? '')
    }).catch(failure => { if (!controller.signal.aborted) { setError(failureMessage(failure)); void logout(failure) } })
    return () => controller.abort()
  }, [id, logout, isPending])
  async function save() {
    setBusy(true); setError('')
    try {
      const fields = { name, sortName: sortName || null, description: description || null, website: website || null }
      const result = id ? await groupClient.update(id, group!.etag!, fields) : await groupClient.create({ ...fields, kind })
      navigate(`/label-groups/${result.id}/show`)
    } catch (failure) { setError(failureMessage(failure)); if (isStale(failure)) setStale(true); await logout(failure) }
    finally { setBusy(false) }
  }
  return <Stack component="form" spacing={2} sx={{ p: 3 }} onSubmit={e => { e.preventDefault(); if (!busy && !stale && name.trim() && (!id || group?.etag)) void save() }}>
    <Typography variant="h4" component="h1">{id ? 'Edit label group' : 'Create label group'}</Typography>
    <Typography>A group is not a release label or a claim of legal ownership. Kind cannot be changed after creation.</Typography>
    {error && <Alert severity="error">{error}</Alert>}
    {id && <Button disabled={busy} onClick={() => void load()}>Reload group</Button>}
    <TextField select label="Group kind" value={kind} disabled={!!id || busy} onChange={e => { if (e.target.value === 'family' || e.target.value === 'organisation') setKind(e.target.value) }}>
      <MenuItem value="family">Family / brand</MenuItem><MenuItem value="organisation">Organisation</MenuItem>
    </TextField>
    <TextField label="Group name" required value={name} disabled={busy || stale || !!id && !group} onChange={e => setName(e.target.value)} />
    <TextField label="Sort name" value={sortName} disabled={busy || stale || !!id && !group} onChange={e => setSortName(e.target.value)} />
    <TextField label="Description" multiline value={description} disabled={busy || stale || !!id && !group} slotProps={{ htmlInput: { maxLength: 12000 } }} onChange={e => setDescription(e.target.value)} />
    <TextField label="Official website" value={website} disabled={busy || stale || !!id && !group} onChange={e => setWebsite(e.target.value)} />
    <Button type="submit" disabled={busy || stale || !name.trim() || !!id && !group?.etag}>Save group</Button>
    <Button component={RouterLink} to={id ? `/label-groups/${id}/show` : '/label-groups'}>Cancel</Button>
  </Stack>
}

export function LabelGroupShow() {
  const { isPending } = useAuthenticated()
  const { id } = useParams()
  const logout = useLogoutIfAccessDenied()
  const [group, setGroup] = useState<LabelGroup>()
  const [error, setError] = useState('')
  const [revision, setRevision] = useState(0)
  useEffect(() => {
    if (!id || isPending) return
    const controller = new AbortController()
    void groupClient.get(id, controller.signal).then(result => { setGroup(result); setError('') }).catch(failure => {
      if (!controller.signal.aborted) { setError(failureMessage(failure)); void logout(failure) }
    })
    return () => controller.abort()
  }, [id, revision, logout, isPending])
  return <Stack spacing={2} sx={{ p: 3, minWidth: 0, overflowWrap: 'anywhere' }}>
    {error && <Alert severity="error">{error}</Alert>}
    {group && group.id === id && <>
      <Typography variant="h4" component="h1">{group.name}</Typography>
      <Typography>{group.kind === 'family' ? 'Family / brand' : 'Organisation'} — affiliation does not imply ownership.</Typography>
      <Typography sx={{ whiteSpace: 'pre-wrap' }}>{group.description || 'No description recorded.'}</Typography>
      {group.website && <Link href={group.website} target="_blank" rel="noopener noreferrer">Official website</Link>}
      <Button component={RouterLink} to={`/label-groups/${group.id}`}>Edit group</Button>
      <Memberships key={group.id} groupId={group.id} onChanged={() => setRevision(r => r + 1)} />
    </>}
  </Stack>
}

export function LabelGroupSections() {
  const record = useRecordContext<LabelRecord>()
  return record ? <Memberships key={record.id} labelId={record.id} /> : null
}

function Memberships({ labelId, groupId, onChanged }: { labelId?: string; groupId?: string; onChanged?: () => void }) {
  const logout = useLogoutIfAccessDenied()
  const [status, setStatus] = useState('current')
  const [page, setPage] = useState(1)
  const [data, setData] = useState<GroupPage<GroupMembership>>()
  const [error, setError] = useState('')
  const [revision, setRevision] = useState(0)
  const [editing, setEditing] = useState<{ id?: string; former?: boolean }>()
  const [relatedOpen, setRelatedOpen] = useState(false)
  useEffect(() => {
    const controller = new AbortController()
    void groupClient.members(labelId, groupId, page, status, controller.signal).then(result => { setData(result); setError('') }).catch(failure => {
      if (!controller.signal.aborted) { setError(failureMessage(failure)); void logout(failure) }
    })
    return () => controller.abort()
  }, [labelId, groupId, status, page, revision, logout])
  const rows = data?.items ?? []
  function row(item: GroupMembership) {
    return <Box key={item.id} sx={{ borderBottom: 1, borderColor: 'divider', py: 1, overflowWrap: 'anywhere' }}>
      <Link component={RouterLink} to={labelId ? `/label-groups/${item.group.id}/show` : `/labels/${item.label.id}/show`}>
        {labelId ? item.group.name : item.label.name || item.label.id}
      </Link>
      <Typography>{item.status}; latest known period: {item.since ?? 'unknown start'} — {item.until ?? 'unknown end'}</Typography>
      <Typography variant="body2">Source: {item.source} {item.sourceUrl && <Link href={item.sourceUrl} target="_blank" rel="noopener noreferrer">Evidence</Link>}</Typography>
      {item.notes && <Typography variant="body2" sx={{ whiteSpace: 'pre-wrap' }}>{item.notes}</Typography>}
      <Button onClick={() => setEditing({ id: item.id })}>Edit membership</Button>
      {item.status !== 'former' && <Button onClick={() => setEditing({ id: item.id, former: true })}>Mark as former member</Button>}
    </Box>
  }
  return <Box component="section" aria-label={labelId ? 'Label groups and families' : 'Group members'}>
    <Typography variant="h6">{labelId ? 'Groups and families' : 'Members'}</Typography>
    <Typography variant="body2">Affiliation only: not ownership, distribution or an imprint relationship.</Typography>
    <Button onClick={() => setEditing({})}>{labelId ? 'Add to group' : 'Add member'}</Button>
    {labelId && <Button onClick={() => setRelatedOpen(true)}>Related labels</Button>}
    <TextField select label="Membership status filter" size="small" value={status} onChange={e => { setStatus(e.target.value); setPage(1) }}>
      {['current', 'former', 'unknown', 'all'].map(value => <MenuItem key={value} value={value}>
        {value}{data?.counts && ` (${value === 'all' ? Object.values(data.counts).reduce((a, b) => a + b, 0) : data.counts[value] ?? 0})`}
      </MenuItem>)}
    </TextField>
    {error && <Alert severity="error">{error}</Alert>}
    {!error && data && <>
      {labelId ? <>
        <Typography variant="subtitle1">Families</Typography>
        {rows.filter(i => i.group.kind === 'family').map(row)}
        <Typography variant="subtitle1">Organisations</Typography>
        {rows.filter(i => i.group.kind === 'organisation').map(row)}
      </> : rows.map(row)}
      {!rows.length && <Typography>No memberships match this filter.</Typography>}
      <Pager page={page} total={data.total} onPage={setPage} />
    </>}
    {editing && <MembershipEditor labelId={labelId} groupId={groupId} membershipId={editing.id} markFormer={editing.former}
      onClose={() => setEditing(undefined)} onSaved={() => { setEditing(undefined); setPage(1); setRevision(r => r + 1); onChanged?.() }} />}
    {relatedOpen && labelId && <RelatedLabels labelId={labelId} onClose={() => setRelatedOpen(false)} />}
  </Box>
}

function MembershipEditor({ labelId, groupId, membershipId, markFormer, onClose, onSaved }: {
  labelId?: string; groupId?: string; membershipId?: string; markFormer?: boolean; onClose: () => void; onSaved: () => void
}) {
  const provider = useDataProvider()
  const logout = useLogoutIfAccessDenied()
  const [current, setCurrent] = useState<GroupMembership>()
  const [label, setLabel] = useState<RelatedLabel>()
  const [group, setGroup] = useState<LabelGroup>()
  const [fields, setFields] = useState<MembershipFields>({ status: 'unknown', since: null, until: null, source: 'manual', sourceUrl: null, notes: null })
  const [statusChosen, setStatusChosen] = useState(false)
  const [search, setSearch] = useState('')
  const [searchPage, setSearchPage] = useState(1)
  const [searchTotal, setSearchTotal] = useState(0)
  const [labels, setLabels] = useState<LabelRecord[]>([])
  const [groups, setGroups] = useState<LabelGroup[]>([])
  const [kind, setKind] = useState('')
  const [externalSource, setExternalSource] = useState('')
  const [externalId, setExternalId] = useState('')
  const [busy, setBusy] = useState(false)
  const [ready, setReady] = useState(false)
  const [stale, setStale] = useState(false)
  const [error, setError] = useState('')
  const [confirmed, setConfirmed] = useState(false)
  const [rejoin, setRejoin] = useState(false)
  const [remove, setRemove] = useState(false)
  async function execute(action: () => Promise<void>) {
    setBusy(true); setError('')
    try { await action() }
    catch (failure) { setError(failureMessage(failure)); if (isStale(failure)) setStale(true); await logout(failure) }
    finally { setBusy(false) }
  }
  const initialize = useCallback(async (signal?: AbortSignal): Promise<{ item?: GroupMembership; label?: RelatedLabel; group?: LabelGroup }> => {
    if (membershipId) {
      const item = await groupClient.membership(membershipId, signal)
      return { item, label: item.label, group: item.group }
    }
    return { label: labelId ? await groupClient.label(labelId, signal) : undefined, group: groupId ? await groupClient.get(groupId, signal) : undefined }
  }, [membershipId, labelId, groupId])
  const applyInitial = useCallback(({ item, label, group }: { item?: GroupMembership; label?: RelatedLabel; group?: LabelGroup }) => {
    setCurrent(item); setLabel(label); setGroup(group)
    if (item) {
      setFields({ status: markFormer ? 'former' : item.status, since: item.since, until: item.until, source: item.source, sourceUrl: item.sourceUrl, notes: item.notes })
      setStatusChosen(true)
    }
    setReady(true); setStale(false)
  }, [markFormer])
  useEffect(() => {
    const controller = new AbortController()
    void initialize(controller.signal).then(result => { if (!controller.signal.aborted) applyInitial(result) }).catch(failure => {
      if (!controller.signal.aborted) { setError(failureMessage(failure)); void logout(failure) }
    })
    return () => controller.abort()
  }, [initialize, applyInitial, logout])
  function change(patch: Partial<MembershipFields>) { setFields(f => ({ ...f, ...patch })); setConfirmed(false) }
  function reload() {
    setReady(false); setConfirmed(false); setRemove(false); setRejoin(false); setLabels([]); setGroups([])
    setSearchPage(1); setSearchTotal(0)
    setLabel(undefined); setGroup(undefined); setCurrent(undefined); setStatusChosen(false)
    setFields({ status: 'unknown', since: null, until: null, source: 'manual', sourceUrl: null, notes: null })
    void execute(async () => applyInitial(await initialize()))
  }
  function find(page: number) {
    void execute(async () => {
      setSearchPage(page)
      if (labelId) {
        const q = new URLSearchParams({ page: String(page), pageSize: '10' }); if (search.trim()) q.set('q', search); if (kind) q.set('kind', kind)
        const result = await groupClient.list(q); setGroups(result.items); setSearchTotal(result.total)
      } else {
        const filter: Record<string, string> = {}
        if (search.trim()) filter.q = search
        if (externalSource.trim()) filter.source = externalSource
        if (externalId.trim()) filter.externalId = externalId
        const result = await provider.getList<LabelRecord>('labels', { pagination: { page, perPage: 10 }, sort: { field: 'name', order: 'ASC' }, filter })
        setLabels(result.data); setSearchTotal(result.total ?? 0)
      }
    })
  }
  function save() {
    if (!label || !group) return
    void execute(async () => {
      if (remove) await groupClient.removeMembership(current!.id, current!.etag!)
      else await groupClient.saveMembership(current?.id, current?.etag, {
        ...fields, since: fields.since || null, until: fields.until || null, sourceUrl: fields.sourceUrl || null, notes: fields.notes || null,
        labelVersion: label.version, groupVersion: group.version, confirmRejoin: rejoin,
        ...(!current ? { labelId: label.id, groupId: group.id } : {}),
      })
      onSaved()
    })
  }
  return <Dialog open fullWidth maxWidth="md" onClose={() => { if (!busy) onClose() }}>
    <DialogTitle>Review group membership</DialogTitle>
    <DialogContent sx={{ overflowWrap: 'anywhere' }}><Stack spacing={2} sx={{ pt: 1 }}>
      <Typography>Affiliation does not imply ownership. Dates describe the latest known membership period; earlier periods remain in history.</Typography>
      {error && <Alert severity="error">{error}</Alert>}
      <Button disabled={busy} onClick={reload}>Reload membership</Button>
      <Typography>Label: {label?.name || label?.id || '(choose a label)'}; Group: {group?.name || '(choose a group)'} {group && `(${group.kind})`}</Typography>
      {!membershipId && <>
        <TextField label={labelId ? 'Find group' : 'Find member label'} value={search} disabled={busy || stale || !ready} onChange={e => setSearch(e.target.value)} />
        {labelId ? <TextField select label="Search group kind" value={kind} onChange={e => setKind(e.target.value)}>
          <MenuItem value="">All kinds</MenuItem><MenuItem value="family">Family / brand</MenuItem><MenuItem value="organisation">Organisation</MenuItem>
        </TextField> : <>
          <TextField label="Provider source (optional)" value={externalSource} onChange={e => setExternalSource(e.target.value)} />
          <TextField label="Provider ID (optional)" value={externalId} onChange={e => setExternalId(e.target.value)} />
        </>}
        <Button disabled={busy || stale || !ready} onClick={() => find(1)}>Search membership candidates</Button>
        {groups.map(g => <Button key={g.id} disabled={busy || stale} onClick={() => { setConfirmed(false); void execute(async () => setGroup(await groupClient.get(g.id))) }}>Select {g.name} ({g.kind})</Button>)}
        {labels.map(l => <Button key={l.id} disabled={busy || stale} onClick={() => { setConfirmed(false); void execute(async () => setLabel(await groupClient.label(l.id))) }}>Select {l.name} ({l.id})</Button>)}
        <Pager page={searchPage} total={searchTotal} onPage={find} disabled={busy || stale || !ready} />
      </>}
      <TextField select label="Membership status" value={statusChosen ? fields.status : ''} disabled={busy || stale || remove || !ready} onChange={e => {
        if (e.target.value === 'current' || e.target.value === 'former' || e.target.value === 'unknown') {
          setStatusChosen(true); change({ status: e.target.value }); setRejoin(false)
        }
      }}>
        <MenuItem value="" disabled>Choose status explicitly</MenuItem>
        <MenuItem value="current">Current</MenuItem><MenuItem value="former">Former</MenuItem><MenuItem value="unknown">Unknown / uncertain</MenuItem>
      </TextField>
      <TextField label="Since (latest period)" helperText="YYYY, YYYY-MM or YYYY-MM-DD. Leave blank if unknown." value={fields.since ?? ''} disabled={busy || stale || remove || !ready} onChange={e => change({ since: e.target.value })} />
      <TextField label="Until (latest period)" helperText="Current membership cannot have an end date. No scheduled future changes." value={fields.until ?? ''} disabled={busy || stale || remove || !ready} onChange={e => change({ until: e.target.value })} />
      <TextField label="Membership source" required value={fields.source} disabled={busy || stale || remove || !ready} slotProps={{ htmlInput: { maxLength: 200 } }} onChange={e => change({ source: e.target.value })} />
      <TextField label="Membership evidence URL" value={fields.sourceUrl ?? ''} disabled={busy || stale || remove || !ready} onChange={e => change({ sourceUrl: e.target.value })} />
      <TextField label="Membership notes" multiline value={fields.notes ?? ''} disabled={busy || stale || remove || !ready} slotProps={{ htmlInput: { maxLength: 2000 } }} onChange={e => change({ notes: e.target.value })} />
      {current?.status === 'former' && fields.status === 'current' && !remove && <FormControlLabel control={<Checkbox checked={rejoin} disabled={busy || stale} onChange={e => {
        setRejoin(e.target.checked); setConfirmed(false)
      }} />} label="I reviewed the return and replaced the former period with the latest known period, rather than assuming continuous membership." />}
      {current && <FormControlLabel control={<Checkbox checked={remove} disabled={busy || stale} onChange={e => { setRemove(e.target.checked); setConfirmed(false) }} />}
        label="Remove this assertion (correction only). For departure, mark as former instead. History is retained." />}
      <FormControlLabel control={<Checkbox checked={confirmed} disabled={busy || stale || !ready} onChange={e => setConfirmed(e.target.checked)} />}
        label={remove ? 'I confirm removal of this membership assertion.' : 'I have reviewed this membership and its evidence.'} />
    </Stack></DialogContent>
    <DialogActions>
      <Button disabled={busy} onClick={onClose}>Cancel</Button>
      <Button disabled={busy || stale || !ready || !label || !group || !confirmed || !remove && (!statusChosen || !fields.source.trim() || current?.status === 'former' && fields.status === 'current' && !rejoin)} onClick={save}>
        {remove ? 'Remove assertion' : 'Save membership'}
      </Button>
    </DialogActions>
  </Dialog>
}

function RelatedLabels({ labelId, onClose }: { labelId: string; onClose: () => void }) {
  const logout = useLogoutIfAccessDenied()
  const [kind, setKind] = useState('')
  const [page, setPage] = useState(1)
  const [data, setData] = useState<GroupPage<RelatedGroupLabel>>()
  const [error, setError] = useState('')
  useEffect(() => {
    const controller = new AbortController()
    void groupClient.related(labelId, page, kind, controller.signal).then(result => { setData(result); setError('') }).catch(failure => {
      if (!controller.signal.aborted) { setError(failureMessage(failure)); void logout(failure) }
    })
    return () => controller.abort()
  }, [labelId, page, kind, logout])
  const captions = { family: 'Same family', organisation: 'Same organisation', parent: 'Shares a recorded parent' }
  return <Dialog open fullWidth maxWidth="md" onClose={onClose}>
    <DialogTitle>Related labels</DialogTitle>
    <DialogContent sx={{ overflowWrap: 'anywhere' }}><Stack spacing={2} sx={{ pt: 1 }}>
      <Typography>Current group affiliation and recorded imprint parents are different reasons. Neither implies legal ownership.</Typography>
      <TextField select label="Related label reason" value={kind} onChange={e => { setKind(e.target.value); setPage(1) }}>
        <MenuItem value="">All reasons</MenuItem>{Object.entries(captions).map(([key, text]) => <MenuItem key={key} value={key}>{text}</MenuItem>)}
      </TextField>
      {error && <Alert severity="error">{error}</Alert>}
      {!error && data?.items.map(item => <Box key={item.label.id}>
        <Link component={RouterLink} to={`/labels/${item.label.id}/show`} onClick={onClose}>{item.label.name || item.label.id}</Link>
        {item.reasons.map(reason => <Typography key={`${reason.kind}/${reason.id}`}>{captions[reason.kind]}: <Link component={RouterLink}
          to={reason.kind === 'parent' ? `/labels/${reason.id}/show` : `/label-groups/${reason.id}/show`} onClick={onClose}>{reason.name}</Link></Typography>)}
      </Box>)}
      {!error && data?.total === 0 && <Typography>No related labels match this filter.</Typography>}
      {data && !error && <Pager page={page} total={data.total} onPage={setPage} />}
    </Stack></DialogContent>
    <DialogActions><Button onClick={onClose}>Close</Button></DialogActions>
  </Dialog>
}
