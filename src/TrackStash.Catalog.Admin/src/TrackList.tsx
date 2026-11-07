import { useEffect, useState } from 'react'
import type { ReactNode } from 'react'
import { Accordion, AccordionDetails, AccordionSummary, Alert, Box, Button, LinearProgress, MenuItem, Stack, Table, TableBody, TableCell,
  TableHead, TableRow, TextField, Tooltip, Typography } from '@mui/material'
import AlbumIcon from '@mui/icons-material/Album'
import ExpandMoreIcon from '@mui/icons-material/ExpandMore'
import { useLogoutIfAccessDenied } from 'react-admin'
import { Link } from 'react-router-dom'
import { trackClient } from './catalog'
import type { CatalogTrack, GroupPage, ReleaseCredit } from './catalog'
import { ArtworkImage } from './LabelArtwork'
import { trackContributors } from './labelPresentation'
import { CreditEvidence, TrackTitleArtists } from './TrackPresentation'
import { defaultTrackSort, formatDuration, trackPageSize, trackQuery, trackSorts } from './trackView'
import type { TrackContext, TrackFilter, TrackView } from './trackView'

function releaseLink(track: CatalogTrack) {
  const title = track.releaseTitle ?? 'Untitled release'
  return track.releaseId ? <Tooltip describeChild title={`View release: ${title}`}>
    <Link to={`/releases/${encodeURIComponent(track.releaseId)}/show`}>{title}</Link>
  </Tooltip> : <Typography component="span" variant="body2" color="text.secondary">No release placement</Typography>
}

function Credits({ items }: { items: ReleaseCredit[] }) {
  return items.length ? <ul>{items.map((credit, index) => <li key={index}>
    <Link to={`/artists/${encodeURIComponent(credit.artistId)}/show`}>{credit.creditedName ?? credit.artistName ?? credit.artistId}</Link> - {credit.role}
    {credit.creditedName && credit.artistName && credit.creditedName !== credit.artistName && ` (canonical: ${credit.artistName})`}
    {credit.identityStatus === 'unresolved_compound' && ' [unresolved compound]'}
  </li>)}</ul> : <Typography variant="body2" color="text.secondary">No credits recorded in this scope.</Typography>
}

function TrackDetails({ track, grouped }: { track: CatalogTrack; grouped: boolean }) {
  return <Accordion disableGutters elevation={0} sx={{ bgcolor: 'transparent', '&::before': { display: 'none' } }}>
    <AccordionSummary expandIcon={<ExpandMoreIcon />} sx={{ minHeight: 28, px: 0, '& .MuiAccordionSummary-content': { my: 0.5 } }}>
      <Typography variant="caption">Track details</Typography>
    </AccordionSummary>
    <AccordionDetails sx={{ px: 0 }}>
      <Typography variant="body2">{grouped && track.releaseCount > 1 ? 'Newest release: ' : ''}{releaseLink(track)}</Typography>
      {track.artistCredits.length > 0 && <Stack direction="row" spacing={1} sx={{ alignItems: 'center', my: 0.5 }}>
        <Typography variant="body2">Credited as: {[...new Set(track.artistCredits.map(c => `${c.creditedName ?? 'canonical name'} (${c.role})`))].join(', ')}</Typography>
        <CreditEvidence credits={track.artistCredits} />
      </Stack>}
      <Typography variant="caption" sx={{ display: 'block', overflowWrap: 'anywhere' }}>ISRC: {track.isrc ?? 'unknown'}<br />Recording ID: {track.recordingId}
        {track.placementId && <><br />Placement ID: {track.placementId}<br />Catalog display order: {track.displayOrder} ({track.orderSource})
          <br />Source position: {track.sourcePosition ?? 'unknown'}<br />Official disc/track: {track.discNumber ?? '?'}/{track.trackNumber ?? '?'}</>}</Typography>
      <Typography variant="subtitle2">Track listing credits</Typography><Credits items={track.credits} />
      <Typography variant="subtitle2">Recording credits</Typography><Credits items={track.recordingCredits} />
      {track.externalReferences.map(r => <Typography variant="caption" sx={{ display: 'block' }} key={`${r.source}:${r.externalId}`}>{r.source}: {r.externalId}</Typography>)}
    </AccordionDetails>
  </Accordion>
}

interface Column { id: string; header: string; render: (track: CatalogTrack, index: number) => ReactNode; cell?: object }
const muted = { whiteSpace: 'nowrap', color: 'text.secondary' }

/** Column definitions shared by every track list; contexts choose a default subset (future: user-customised columns). */
function columns(context: TrackContext, grouped: boolean, offset: number): Column[] {
  const all: Column[] = [
    { id: 'number', header: '#', cell: { color: 'text.secondary', verticalAlign: 'top', pt: 2 }, render: (_, index) => offset + index + 1 },
    { id: 'title', header: 'Title / Artist · Remixer', render: track => <Stack direction="row" spacing={1} sx={{ alignItems: 'flex-start' }}>
      {context !== 'release' && <Box sx={{ width: 40, height: 40, flexShrink: 0, mt: 0.5 }}>
        {track.coverAssetId ? <ArtworkImage assetId={track.coverAssetId} caption={`${track.releaseTitle ?? 'Release'} cover thumbnail`} size={40} />
          : <Box sx={{ width: 40, height: 40, bgcolor: 'action.hover', display: 'flex', alignItems: 'center', justifyContent: 'center' }}><AlbumIcon color="disabled" /></Box>}
      </Box>}
      <Stack spacing={0.5} sx={{ minWidth: 220, flex: 1 }}>
        <TrackTitleArtists title={track.title} mixName={track.mixName} credits={trackContributors(track.credits, track.recordingCredits)}
          to={context === 'recording' ? undefined : `/recordings/${encodeURIComponent(track.recordingId)}/show${track.releaseId ? `?${new URLSearchParams({ releaseId: track.releaseId })}` : ''}`} />
        <TrackDetails track={track} grouped={grouped} />
      </Stack>
    </Stack> },
    { id: 'release', header: 'Release', render: track => <Typography variant="body2">{releaseLink(track)}</Typography> },
    { id: 'label', header: 'Label / Genre · Subgenre', render: track => <>
      {track.labels.length ? <Stack>{track.labels.map(label => <Tooltip describeChild key={label.id} title={`View Label: ${label.name ?? 'Unnamed Label'}${label.isPrimary ? ' (primary label)' : ''}`}>
        <Link to={`/labels/${encodeURIComponent(label.id)}/show`}>{label.name ?? 'Unnamed Label'}</Link>
      </Tooltip>)}</Stack> : <Typography variant="body2" color="text.secondary">Not recorded</Typography>}
      <Typography variant="body2" color="text.secondary">{[track.musicalMetadata?.genres?.join(', ') || track.genre,
        track.musicalMetadata?.subgenres?.join(', ') || track.subgenre].filter(Boolean).join(' / ') || 'Genre not available'}</Typography>
      {track.musicalMetadata?.preferredGenre && <Typography variant="body2">Preferred: {track.musicalMetadata.preferredGenre}</Typography>}
    </> },
    { id: 'date', header: 'Release date', cell: muted, render: track => track.publishDate ?? track.newReleaseDate ?? 'Not available' },
    { id: 'bpm', header: 'BPM', cell: muted, render: track => track.musicalMetadata?.bpm ?? track.bpm ?? '—' },
    { id: 'key', header: 'Key', cell: muted, render: track => track.musicalMetadata?.keyValue
      ? `${track.musicalMetadata.keyValue} (${track.musicalMetadata.keyNotation})` : track.keyName ?? '—' },
    { id: 'length', header: 'Length', cell: muted, render: track => formatDuration(track.durationMs) },
    { id: 'releases', header: 'Releases', cell: muted, render: track => track.releaseCount },
  ]
  const hidden = new Set(context === 'release' ? ['release', 'label', 'date'] : [])
  if (!grouped) hidden.add('releases')
  return all.filter(column => !hidden.has(column.id))
}

function Pager({ page, total, onPage }: { page: number; total: number; onPage: (page: number) => void }) {
  return <Stack direction="row" spacing={2} useFlexGap sx={{ alignItems: 'center', my: 2, flexWrap: 'wrap' }}>
    <Button disabled={page <= 1} onClick={() => onPage(page - 1)}>Previous page</Button>
    <Typography>Page {page}; {total} results</Typography>
    <Button disabled={page * trackPageSize >= total} onClick={() => onPage(page + 1)}>Next page</Button>
  </Stack>
}

function TrackRows({ filter, view, context, grouped, label, onPage }: {
  filter: TrackFilter; view: TrackView; context: TrackContext; grouped: boolean; label: string; onPage: (page: number) => void
}) {
  const [data, setData] = useState<GroupPage<CatalogTrack>>()
  const [error, setError] = useState('')
  const logout = useLogoutIfAccessDenied()
  const query = trackQuery(filter, view, grouped).toString()
  useEffect(() => {
    const controller = new AbortController()
    void trackClient.list(new URLSearchParams(query), controller.signal).then(result => { if (!controller.signal.aborted) setData(result) }).catch(failure => {
      if (!controller.signal.aborted) { setError(failure instanceof Error ? failure.message : 'Tracks could not be loaded.'); void logout(failure) }
    })
    return () => controller.abort()
  }, [query, logout])
  if (error) return <Alert severity="error">{error}</Alert>
  if (!data) return <LinearProgress aria-label="Loading tracks" />
  const shown = columns(context, grouped, (data.page - 1) * data.pageSize)
  return <>
    {!data.total && <Typography>{view.q.trim() ? 'No tracks match this search.' : 'No tracks recorded.'}</Typography>}
    <Box sx={{ overflowX: 'auto', width: '100%', minWidth: 0, contain: 'inline-size' }}><Table size="small" aria-label={label}>
      <TableHead><TableRow>{shown.map(column => <TableCell key={column.id}>{column.header}</TableCell>)}</TableRow></TableHead>
      <TableBody>{data.items.map((track, index) => <TableRow hover key={track.placementId ?? `recording:${track.recordingId}`}>
        {shown.map(column => <TableCell key={column.id} sx={column.cell}>{column.render(track, index)}</TableCell>)}
      </TableRow>)}</TableBody>
    </Table></Box>
    <Pager page={view.page} total={data.total} onPage={onPage} />
  </>
}

/** The single track list used for releases, Labels, Artists, Recordings and the top-level Tracks page. */
export function TrackList({ filter, context, view, onChange, label, groupByRecording = false }: {
  filter: TrackFilter; context: TrackContext; view: TrackView; onChange: (view: TrackView) => void; label: string; groupByRecording?: boolean
}) {
  const [search, setSearch] = useState(view.q)
  const [appliedSearch, setAppliedSearch] = useState(view.q)
  if (appliedSearch !== view.q) { setAppliedSearch(view.q); setSearch(view.q) }
  useEffect(() => {
    if (search === view.q) return
    const timer = setTimeout(() => onChange({ ...view, q: search, page: 1 }), 300)
    return () => clearTimeout(timer)
  }, [search, view, onChange])
  const sorts = trackSorts.filter(s => s.value !== 'order' || context === 'release')
  return <Stack spacing={2} sx={{ minWidth: 0 }}>
    <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2}>
      <TextField label="Search tracks" value={search} onChange={e => setSearch(e.target.value)} slotProps={{ htmlInput: { maxLength: 200 } }} sx={{ flex: { sm: '1 1 320px' }, minWidth: 0 }}
        helperText="Title, mix, ISRC, release or Artist; every word must match" />
      <TextField select label="Sort tracks" value={view.sort} onChange={e => onChange({ ...view, sort: e.target.value, page: 1 })} sx={{ flex: { sm: '0 0 240px' } }}>
        {sorts.map(sort => <MenuItem key={sort.value} value={sort.value}>{sort.label}</MenuItem>)}
      </TextField>
    </Stack>
    <TrackRows filter={filter} view={view} context={context} grouped={groupByRecording} label={label} onPage={page => onChange({ ...view, page })} />
  </Stack>
}

/** Track list whose view state lives in component state (tabs whose URLs are not track-specific). */
export function LocalTrackList(props: { filter: TrackFilter; context: TrackContext; label: string; groupByRecording?: boolean }) {
  const [view, setView] = useState<TrackView>({ page: 1, q: '', sort: defaultTrackSort(props.context) })
  return <TrackList {...props} view={view} onChange={setView} />
}
