import { useEffect, useState } from 'react'
import { Alert, Box, Button, LinearProgress, Stack, Table, TableBody, TableCell, TableHead, TableRow, TextField, Typography } from '@mui/material'
import { Title, useAuthenticated, useLogoutIfAccessDenied } from 'react-admin'
import { Link, useParams, useSearchParams } from 'react-router-dom'
import { releaseClient } from './catalog'
import type { CatalogRelease, GroupPage, ReleaseCredit, ReleaseDetail, ReleaseTrack } from './catalog'

function Pager({ page, total, onPage }: { page: number; total: number; onPage: (page: number) => void }) {
  return <Stack direction="row" spacing={2} sx={{ alignItems: 'center', my: 2 }}>
    <Button disabled={page <= 1} onClick={() => onPage(page - 1)}>Previous page</Button>
    <Typography>Page {page}; {total} results</Typography>
    <Button disabled={page * 25 >= total} onClick={() => onPage(page + 1)}>Next page</Button>
  </Stack>
}
function ReleasePage({ page, search, labelId, onPage }: { page: number; search: string; labelId?: string; onPage: (page: number) => void }) {
  const [data, setData] = useState<GroupPage<CatalogRelease>>()
  const [error, setError] = useState('')
  const logout = useLogoutIfAccessDenied()
  useEffect(() => {
    const controller = new AbortController()
    const query = new URLSearchParams({ page: String(page), pageSize: '25', q: search })
    if (labelId) query.set('labelId', labelId)
    void releaseClient.list(query, controller.signal).then(result => {
      if (!controller.signal.aborted) setData(result)
    }).catch(failure => {
      if (!controller.signal.aborted) { setError(failure instanceof Error ? failure.message : 'Releases could not be loaded.'); void logout(failure) }
    })
    return () => controller.abort()
  }, [page, search, labelId, logout])
  if (error) return <Alert severity="error">{error}</Alert>
  if (!data) return <LinearProgress aria-label="Loading catalog releases" />
  return <>
    {!data.total && <Typography>No catalog releases match this view.</Typography>}
    <Box sx={{ overflowX: 'auto' }}>
      <Table aria-label={labelId ? 'Label releases' : 'Catalog releases'}>
        <TableHead><TableRow><TableCell>Release</TableCell><TableCell>Label</TableCell><TableCell>Catalog number</TableCell><TableCell>Publish date</TableCell><TableCell>Tracks</TableCell></TableRow></TableHead>
        <TableBody>{data.items.map(release => <TableRow key={release.id} hover>
          <TableCell><Link to={`/releases/${encodeURIComponent(release.id)}/show`}>{release.title ?? 'Untitled release'}</Link></TableCell>
          <TableCell>{release.labelId ? <Link to={`/labels/${encodeURIComponent(release.labelId)}/show`}>{release.labelName ?? release.labelId}</Link> : 'Not recorded'}</TableCell>
          <TableCell>{release.catalogNumber ?? 'Not recorded'}</TableCell><TableCell>{release.publishDate ?? 'Unknown'}</TableCell><TableCell>{release.placementCount}</TableCell>
        </TableRow>)}</TableBody>
      </Table>
    </Box>
    <Pager page={page} total={data.total} onPage={onPage} />
  </>
}
export function LabelReleases({ labelId }: { labelId: string }) {
  const [search, setSearch] = useState('')
  const [page, setPage] = useState(1)
  return <Stack spacing={2}>
    <TextField label="Search label releases" value={search} onChange={e => { setSearch(e.target.value); setPage(1) }}
      slotProps={{ htmlInput: { maxLength: 200 } }} />
    <ReleasePage key={`${labelId}:${page}:${search}`} page={page} search={search} labelId={labelId} onPage={setPage} />
  </Stack>
}
export function ReleaseList() {
  const { isPending } = useAuthenticated()
  const [params, setParams] = useSearchParams()
  const search = params.get('q') ?? ''
  const labelId = params.get('labelId') ?? undefined
  const value = Number(params.get('page') ?? 1)
  const page = Number.isSafeInteger(value) && value > 0 ? value : 1
  function change(field: string, value: string) {
    const next = new URLSearchParams(params); next.set(field, value)
    if (field !== 'page') next.set('page', '1')
    setParams(next, { replace: true })
  }
  return <Stack spacing={2} sx={{ p: 3 }}>
    <Title title="Releases" /><Typography component="h1" variant="h4">Releases</Typography>
    <Typography>Canonical releases and track placements, independent of purchased tracks or media files.</Typography>
    <TextField label="Search releases" value={search} onChange={e => change('q', e.target.value)} slotProps={{ htmlInput: { maxLength: 200 } }} />
    {labelId && <Alert severity="info" action={<Button onClick={() => { const next = new URLSearchParams(params); next.delete('labelId'); next.set('page', '1'); setParams(next) }}>Clear label filter</Button>}>Filtered to label {labelId}</Alert>}
    {!isPending && <ReleasePage key={`${page}:${search}:${labelId}`} page={page} search={search} labelId={labelId} onPage={p => change('page', String(p))} />}
  </Stack>
}
function Credits({ items }: { items: ReleaseCredit[] }) {
  return items.length ? <ul>{items.map((credit, index) => <li key={index}>
    {credit.creditedName ?? credit.artistName ?? credit.artistId} - {credit.role}
    {credit.creditedName && credit.artistName && credit.creditedName !== credit.artistName && ` (canonical: ${credit.artistName})`}
    {credit.identityStatus === 'unresolved_compound' && ' [unresolved compound]'}
  </li>)}</ul> : <Typography color="text.secondary">No credits recorded in this scope.</Typography>
}
function Tracks({ id, page, onPage }: { id: string; page: number; onPage: (page: number) => void }) {
  const [data, setData] = useState<GroupPage<ReleaseTrack>>()
  const [error, setError] = useState('')
  const logout = useLogoutIfAccessDenied()
  useEffect(() => {
    const controller = new AbortController()
    void releaseClient.tracks(id, page, controller.signal).then(result => { if (!controller.signal.aborted) setData(result) }).catch(failure => {
      if (!controller.signal.aborted) { setError(failure instanceof Error ? failure.message : 'Release tracks could not be loaded.'); void logout(failure) }
    })
    return () => controller.abort()
  }, [id, page, logout])
  if (error) return <Alert severity="error">{error}</Alert>
  if (!data) return <LinearProgress aria-label="Loading catalog release tracks" />
  return <>
    {!data.total && <Typography>No track placements recorded.</Typography>}
    <Box sx={{ overflowX: 'auto' }}><Table aria-label="Catalog release tracks">
      <TableHead><TableRow><TableCell>Order</TableCell><TableCell>Track / version</TableCell><TableCell>Placement credits</TableCell><TableCell>Source identity</TableCell></TableRow></TableHead>
      <TableBody>{data.items.map(track => <TableRow key={track.placementId}>
        <TableCell>{track.displayOrder} ({track.orderSource})<Typography variant="body2">Source position: {track.sourcePosition ?? 'unknown'}<br />Official disc/track: {track.discNumber ?? '?'}/{track.trackNumber ?? '?'}</Typography></TableCell>
        <TableCell>{track.title ?? 'Untitled recording'}<br />{track.mixName}
          <Typography variant="body2">ISRC: {track.isrc ?? 'unknown'}; duration: {track.durationMs === null ? 'unknown' : `${track.durationMs} ms`}<br />Recording ID: {track.recordingId}</Typography></TableCell>
        <TableCell><Credits items={track.credits} /></TableCell>
        <TableCell>{track.externalReferences.map(r => <Typography key={`${r.source}:${r.externalId}`}>{r.source}: {r.externalId}</Typography>)}</TableCell>
      </TableRow>)}</TableBody>
    </Table></Box>
    <Pager page={page} total={data.total} onPage={onPage} />
  </>
}
function Detail({ id }: { id: string }) {
  const [data, setData] = useState<ReleaseDetail>()
  const [page, setPage] = useState(1)
  const [error, setError] = useState('')
  const logout = useLogoutIfAccessDenied()
  useEffect(() => {
    const controller = new AbortController()
    void releaseClient.get(id, controller.signal).then(result => { if (!controller.signal.aborted) setData(result) }).catch(failure => {
      if (!controller.signal.aborted) { setError(failure instanceof Error ? failure.message : 'Release could not be loaded.'); void logout(failure) }
    })
    return () => controller.abort()
  }, [id, logout])
  if (error) return <Alert severity="error">{error}</Alert>
  if (!data) return <LinearProgress aria-label="Loading catalog release" />
  return <Stack spacing={2}>
    <Title title={data.title ?? 'Release'} /><Typography component="h1" variant="h4">{data.title ?? 'Untitled release'}</Typography>
    <Alert severity="info">Catalog metadata only. Track placements do not establish purchases, media availability or NAS backup coverage.</Alert>
    <Stack direction="row" spacing={2}>{data.labels.map(label =>
      <Link key={label.id} to={`/labels/${encodeURIComponent(label.id)}/show`}>{label.name ?? label.id}{label.isPrimary ? ' (primary label)' : ''}</Link>)}</Stack>
    <Typography>Catalog number: {data.catalogNumber ?? 'Unknown'}; UPC: {data.upc ?? 'Unknown'}</Typography>
    <Typography>Publish date: {data.publishDate ?? 'Unknown'}; new release date: {data.newReleaseDate ?? 'Unknown'}</Typography>
    <Typography component="h2" variant="h6">Release credits</Typography><Credits items={data.credits} />
    <Typography component="h2" variant="h6">Tracks</Typography>
    <Typography>Display order is separate from source sequence and official disc/track numbers. Unknown numbers are not inferred.</Typography>
    <Tracks key={`${id}:${page}`} id={id} page={page} onPage={setPage} />
    <Typography component="h2" variant="h6">Release identities</Typography>
    <Typography>Catalog ID: {data.id}</Typography>
    {data.externalReferences.map(r => <Typography key={`${r.source}:${r.externalId}`}>{r.source}: {r.externalId}{r.isPrimary ? ' (primary)' : ''}</Typography>)}
  </Stack>
}
export function ReleaseShow() {
  const { id } = useParams()
  const { isPending } = useAuthenticated()
  return <Box sx={{ p: 3 }}><Link to="/releases">All releases</Link>
    {!isPending && id && <Detail key={id} id={id} />}
  </Box>
}
