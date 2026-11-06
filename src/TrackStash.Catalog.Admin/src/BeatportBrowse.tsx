import { Fragment, useEffect, useRef, useState } from 'react'
import type { FormEvent } from 'react'
import {
  Alert, Box, Button, Chip, IconButton, LinearProgress, MenuItem, Stack, Table, TableBody, TableCell, TableHead, TableRow,
  TextField, ToggleButton, ToggleButtonGroup, Tooltip, Typography,
} from '@mui/material'
import ExpandLess from '@mui/icons-material/ExpandLess'
import ExpandMore from '@mui/icons-material/ExpandMore'
import OpenInNew from '@mui/icons-material/OpenInNew'
import { Title, useAuthenticated, useLogoutIfAccessDenied } from 'react-admin'
import { Link as RouterLink, useSearchParams } from 'react-router-dom'
import { beatportClient } from './catalog'
import { formatLength } from './beatportFormat'
import type { BeatportLabelItem, BeatportLabelPage, BeatportRef, BeatportReleaseDetail, BeatportReleaseItem, BeatportReleasePage, BeatportReleaseSearch, BeatportReleaseSort } from './catalog'

const sorts: { value: BeatportReleaseSort; label: string }[] = [
  { value: 'newest', label: 'Newest first' }, { value: 'oldest', label: 'Oldest first' }, { value: 'name', label: 'Name' },
]

const positive = (value: string | null) => value && /^[1-9][0-9]{0,17}$/.test(value) ? Number(value) : undefined
const names = (refs: BeatportRef[]) => refs.map(ref => ref.name).join(', ')


function problem(failure: unknown, fallback: string) {
  const code = (failure as { body?: { code?: string } })?.body?.code
  const text = failure instanceof Error ? failure.message : fallback
  if (code === 'beatport_sign_in_required')
    return <>Sign in to Beatport first, from the <RouterLink to="/purchase-releases">Purchase releases</RouterLink> page.</>
  if (code === 'beatport_broker_not_configured' || code === 'beatport_broker_unavailable')
    return <>{text} Browsing Beatport needs the broker that start-prod.sh starts.</>
  return text
}

// Loads data for a request; changing the request (compared as JSON) aborts the previous load. Loaders must be module-level so they stay stable.
function useLoad<R, T>(request: R | undefined, loader: (request: R, signal: AbortSignal) => Promise<T>) {
  const key = request === undefined ? undefined : JSON.stringify(request)
  const [state, setState] = useState<{ key?: string; data?: T; error?: unknown }>({})
  const logoutIfDenied = useLogoutIfAccessDenied()
  const logout = useRef(logoutIfDenied)
  useEffect(() => { logout.current = logoutIfDenied }, [logoutIfDenied])
  useEffect(() => {
    if (key === undefined) return
    const controller = new AbortController()
    loader(JSON.parse(key) as R, controller.signal)
      .then(data => { if (!controller.signal.aborted) setState({ key, data }) })
      .catch(error => { if (!controller.signal.aborted) { setState({ key, error }); void logout.current(error) } })
    return () => controller.abort()
  }, [key, loader])
  if (key === undefined) return { loading: false }
  const current = state.key === key
  return { data: current ? state.data : undefined, error: current ? state.error : undefined, loading: !current }
}

const loadRelease = (id: number, signal: AbortSignal) => beatportClient.release(id, signal)
const loadLabel = (id: number, signal: AbortSignal) => beatportClient.label(id, signal)
const loadReleases = (search: BeatportReleaseSearch, signal: AbortSignal) => beatportClient.releases(search, signal)
const loadLabels = ({ q, page }: { q: string; page: number }, signal: AbortSignal) => beatportClient.labels(q, page, signal)

function CatalogLinks({ ids, kind, text }: { ids: string[]; kind: 'releases' | 'labels'; text: string }) {
  return <>{ids.map(id => <Chip key={id} size="small" color="success" clickable component={RouterLink} to={`/${kind}/${encodeURIComponent(id)}/show`}
    label={ids.length > 1 ? `${text} (${id})` : text} />)}</>
}

function ReleaseStatus({ item }: { item: BeatportReleaseItem }) {
  return <Stack direction="row" spacing={0.5} useFlexGap sx={{ flexWrap: 'wrap' }}>
    <CatalogLinks ids={item.catalogReleaseIds} kind="releases" text="In catalog" />
    {item.inPurchaseHistory && <Tooltip title={item.purchaseSourceComplete ? 'In your Beatport purchase history' : 'In your purchase history; Beatport data still incomplete'}>
      <Chip size="small" color="info" label="Purchased" />
    </Tooltip>}
    {!item.catalogReleaseIds.length && !item.inPurchaseHistory && <Chip size="small" variant="outlined" label="Not owned" />}
  </Stack>
}

function BeatportLink({ url, label }: { url: string; label: string }) {
  return <Tooltip title="Open on Beatport in a new tab">
    <IconButton size="small" component="a" href={url} target="_blank" rel="noopener noreferrer" aria-label={label}><OpenInNew fontSize="small" /></IconButton>
  </Tooltip>
}

function ReleaseTracks({ id }: { id: number }) {
  const { data, error, loading } = useLoad<number, BeatportReleaseDetail>(id, loadRelease)
  if (loading && !data) return <LinearProgress aria-label="Loading Beatport tracks" sx={{ my: 1 }} />
  if (error) return <Alert severity="error" sx={{ my: 1 }}>{problem(error, 'Tracks could not be loaded.')}</Alert>
  if (!data) return null
  return <Box sx={{ overflowX: 'auto', py: 1 }}>
    <Table size="small" aria-label={`Tracks on ${data.release.name}`}>
      <TableHead><TableRow>
        <TableCell>#</TableCell><TableCell>Track</TableCell><TableCell>Artists</TableCell><TableCell>BPM</TableCell>
        <TableCell>Key</TableCell><TableCell>Length</TableCell><TableCell>Genre</TableCell><TableCell>ISRC</TableCell><TableCell />
      </TableRow></TableHead>
      <TableBody>{data.tracks.map(track => <TableRow key={track.id}>
        <TableCell>{track.number ?? ''}</TableCell>
        <TableCell>{track.name}{track.mixName ? ` (${track.mixName})` : ''}</TableCell>
        <TableCell>{names(track.artists)}{track.remixers.length ? `; remix: ${names(track.remixers)}` : ''}</TableCell>
        <TableCell>{track.bpm ?? ''}</TableCell><TableCell>{track.key ?? ''}</TableCell><TableCell>{formatLength(track.lengthMs)}</TableCell>
        <TableCell>{track.genre ?? ''}</TableCell><TableCell>{track.isrc ?? ''}</TableCell>
        <TableCell padding="checkbox"><BeatportLink url={track.beatportUrl} label={`Open ${track.name} on Beatport`} /></TableCell>
      </TableRow>)}</TableBody>
    </Table>
    {data.trackTotal > data.tracks.length && <Typography variant="body2">Showing {data.tracks.length} of {data.trackTotal} tracks.</Typography>}
  </Box>
}

function Pager({ page, pageSize, total, onPage }: { page: number; pageSize: number; total: number; onPage: (page: number) => void }) {
  const pages = Math.max(1, Math.ceil(total / pageSize))
  return <Stack direction="row" spacing={2} sx={{ my: 2, alignItems: 'center' }}>
    <Button disabled={page <= 1} onClick={() => onPage(page - 1)}>Previous page</Button>
    <Typography>Page {page} of {pages}</Typography>
    <Button disabled={page >= pages} onClick={() => onPage(page + 1)}>Next page</Button>
  </Stack>
}

function ReleaseResults({ data, onLabel, onPage }: { data: BeatportReleasePage; onLabel: (id: number) => void; onPage: (page: number) => void }) {
  const [expanded, setExpanded] = useState<number>()
  return <>
    <Typography>{data.total} Beatport releases</Typography>
    {!data.items.length && <Typography>No matching releases.</Typography>}
    <Box sx={{ overflowX: 'auto' }}>
      <Table size="small" aria-label="Beatport releases">
        <TableHead><TableRow>
          <TableCell padding="checkbox">Tracks</TableCell><TableCell /><TableCell>Release</TableCell><TableCell>Label</TableCell>
          <TableCell>Catalog no. / date</TableCell><TableCell>Yours</TableCell><TableCell />
        </TableRow></TableHead>
        <TableBody>{data.items.map(item => {
          const open = expanded === item.id
          return <Fragment key={item.id}>
            <TableRow hover selected={open}>
              <TableCell padding="checkbox">
                <IconButton size="small" aria-expanded={open} aria-label={`${open ? 'Hide' : 'Show'} tracks for ${item.name}`}
                  onClick={() => setExpanded(open ? undefined : item.id)}>{open ? <ExpandLess /> : <ExpandMore />}</IconButton>
              </TableCell>
              <TableCell sx={{ width: 56 }}>{item.imageUrl && <img src={item.imageUrl} alt="" width={48} height={48} loading="lazy" referrerPolicy="no-referrer" style={{ display: 'block', borderRadius: 4 }} />}</TableCell>
              <TableCell sx={{ overflowWrap: 'anywhere' }}>
                <Typography>{item.name}</Typography>
                <Typography variant="body2" color="text.secondary">{names(item.artists)}{item.type ? ` · ${item.type}` : ''}{item.trackCount ? ` · ${item.trackCount} tracks` : ''}</Typography>
              </TableCell>
              <TableCell>{item.label && <Stack spacing={0.5} sx={{ alignItems: 'flex-start' }}>
                <Button size="small" onClick={() => onLabel(item.label!.id)} aria-label={`Browse ${item.label.name} releases`} sx={{ textTransform: 'none', p: 0, minWidth: 0 }}>{item.label.name}</Button>
                <CatalogLinks ids={item.catalogLabelIds} kind="labels" text="Catalog label" />
              </Stack>}</TableCell>
              <TableCell><Typography variant="body2">{item.catalogNumber ?? ''}</Typography><Typography variant="body2" color="text.secondary">{item.publishDate ?? ''}</Typography></TableCell>
              <TableCell><ReleaseStatus item={item} /></TableCell>
              <TableCell padding="checkbox"><BeatportLink url={item.beatportUrl} label={`Open ${item.name} on Beatport`} /></TableCell>
            </TableRow>
            {open && <TableRow><TableCell colSpan={7} sx={{ bgcolor: 'action.hover' }}><ReleaseTracks id={item.id} /></TableCell></TableRow>}
          </Fragment>
        })}</TableBody>
      </Table>
    </Box>
    <Pager page={data.page} pageSize={data.pageSize} total={data.total} onPage={onPage} />
  </>
}

function LabelResults({ data, onLabel, onPage }: { data: BeatportLabelPage; onLabel: (id: number) => void; onPage: (page: number) => void }) {
  return <>
    <Typography>{data.total} Beatport labels</Typography>
    {!data.items.length && <Typography>No matching labels.</Typography>}
    <Table size="small" aria-label="Beatport labels">
      <TableHead><TableRow><TableCell>Label</TableCell><TableCell>In catalog</TableCell><TableCell /><TableCell /></TableRow></TableHead>
      <TableBody>{data.items.map(label => <TableRow key={label.id} hover>
        <TableCell sx={{ overflowWrap: 'anywhere' }}>{label.name}<Typography variant="body2" color="text.secondary">Beatport {label.id}</Typography></TableCell>
        <TableCell><CatalogLinks ids={label.catalogLabelIds} kind="labels" text="Catalog label" /></TableCell>
        <TableCell><Button size="small" onClick={() => onLabel(label.id)}>Browse releases</Button></TableCell>
        <TableCell padding="checkbox"><BeatportLink url={label.beatportUrl} label={`Open ${label.name} on Beatport`} /></TableCell>
      </TableRow>)}</TableBody>
    </Table>
    <Pager page={data.page} pageSize={data.pageSize} total={data.total} onPage={onPage} />
  </>
}

function LabelFilter({ id, onClear }: { id: number; onClear: () => void }) {
  const { data, error } = useLoad<number, BeatportLabelItem>(id, loadLabel)
  return <Stack direction="row" spacing={1} useFlexGap sx={{ flexWrap: 'wrap', alignItems: 'center', my: 1 }} aria-label="Label filter">
    <Typography>Label: <strong>{data?.name ?? (error ? `Beatport ${id}` : '…')}</strong></Typography>
    {data && <CatalogLinks ids={data.catalogLabelIds} kind="labels" text="Catalog label" />}
    {data && <BeatportLink url={data.beatportUrl} label={`Open ${data.name} on Beatport`} />}
    <Button size="small" onClick={onClear}>Clear label</Button>
  </Stack>
}

function SearchBox({ initial, label, onSearch }: { initial: string; label: string; onSearch: (value: string) => void }) {
  const [text, setText] = useState(initial)
  return <Stack component="form" direction="row" spacing={1} sx={{ alignItems: 'center' }} aria-label="Search Beatport"
    onSubmit={(event: FormEvent) => { event.preventDefault(); onSearch(text.trim()) }}>
    <TextField label={label} value={text} onChange={event => setText(event.target.value)} slotProps={{ htmlInput: { maxLength: 200 } }} />
    <Button type="submit" variant="contained">Search</Button>
  </Stack>
}

/** Read-only Beatport catalog browser; every request goes through the catalog API and the local broker's cache. */
export function BeatportBrowse() {
  const { isPending } = useAuthenticated()
  return isPending ? null : <BeatportBrowsePage />
}

function BeatportBrowsePage() {
  const [params, setParams] = useSearchParams()
  const mode = params.get('mode') === 'labels' ? 'labels' : 'releases'
  const q = params.get('q')?.trim() ?? ''
  const labelId = positive(params.get('labelId'))
  const sort = sorts.find(option => option.value === params.get('sort'))?.value ?? 'newest'
  const page = positive(params.get('page')) ?? 1

  const update = (changes: Record<string, string | number | undefined>) => setParams(current => {
    const next = new URLSearchParams(current)
    for (const [key, value] of Object.entries(changes)) {
      if (value === undefined || value === '') next.delete(key)
      else next.set(key, String(value))
    }
    return next
  })
  const browseLabel = (id: number) => update({ mode: undefined, labelId: id, q: undefined, page: undefined })

  const releases = useLoad<BeatportReleaseSearch, BeatportReleasePage>(mode === 'releases' && (q || labelId)
    ? { q: q || undefined, labelId, sort, page, pageSize: 25 } : undefined, loadReleases)
  const labels = useLoad<{ q: string; page: number }, BeatportLabelPage>(mode === 'labels' && q ? { q, page } : undefined, loadLabels)
  const current = mode === 'releases' ? releases : labels

  return <Box sx={{ p: 2, minWidth: 0 }}>
    <Title title="Beatport" />
    <Typography variant="h4" component="h1">Beatport</Typography>
    <Alert severity="info" sx={{ my: 2 }}>Browsing is read-only: results come through the local Beatport broker, which caches them, and nothing is added to the catalog.</Alert>
    <Stack direction="row" spacing={2} useFlexGap sx={{ flexWrap: 'wrap', alignItems: 'center' }}>
      <ToggleButtonGroup exclusive size="small" value={mode} aria-label="Search for"
        onChange={(_, value: string | null) => { if (value) update({ mode: value === 'labels' ? 'labels' : undefined, page: undefined, labelId: undefined }) }}>
        <ToggleButton value="releases">Releases</ToggleButton>
        <ToggleButton value="labels">Labels</ToggleButton>
      </ToggleButtonGroup>
      <SearchBox key={`${mode}|${q}`} initial={q} label={mode === 'labels' ? 'Label name' : 'Release name'} onSearch={value => update({ q: value, page: undefined })} />
      {mode === 'releases' && <TextField select size="small" label="Order" value={sort} onChange={event => update({ sort: event.target.value === 'newest' ? undefined : event.target.value, page: undefined })} sx={{ minWidth: 150 }}>
        {sorts.map(option => <MenuItem key={option.value} value={option.value}>{option.label}</MenuItem>)}
      </TextField>}
    </Stack>
    {mode === 'releases' && labelId && <LabelFilter id={labelId} onClear={() => update({ labelId: undefined, page: undefined })} />}
    {current.loading && <LinearProgress aria-label="Loading Beatport results" sx={{ my: 1 }} />}
    {current.error !== undefined && <Alert severity="error" sx={{ my: 1 }}>{problem(current.error, 'Beatport search failed.')}</Alert>}
    {!current.data && !current.loading && current.error === undefined && <Typography sx={{ my: 2 }} color="text.secondary">
      {mode === 'labels' ? 'Search for a label by name.' : 'Search for a release by name, or pick a label to see its releases.'}
    </Typography>}
    <Box sx={{ my: 2 }}>
      {mode === 'releases' && releases.data && <ReleaseResults data={releases.data} onLabel={browseLabel} onPage={next => update({ page: next })} />}
      {mode === 'labels' && labels.data && <LabelResults data={labels.data} onLabel={browseLabel} onPage={next => update({ page: next })} />}
    </Box>
  </Box>
}
