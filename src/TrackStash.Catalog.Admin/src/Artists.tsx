import { useEffect, useState } from 'react'
import { Accordion, AccordionDetails, AccordionSummary, Alert, Box, Button, Chip, LinearProgress, MenuItem, Stack,
  Table, TableBody, TableCell, TableHead, TableRow, TextField, Typography } from '@mui/material'
import ExpandMoreIcon from '@mui/icons-material/ExpandMore'
import { Title, useAuthenticated, useLogoutIfAccessDenied } from 'react-admin'
import { Link, useParams, useSearchParams } from 'react-router-dom'
import { artistClient } from './catalog'
import type { ArtistAppearance, ArtistDetail, CatalogArtist, GroupPage, ReleaseCredit } from './catalog'

export function ArtistCreditName({ credit, showStatus = true }: { credit: ReleaseCredit; showStatus?: boolean }) {
  return <><Link to={`/artists/${encodeURIComponent(credit.artistId)}/show`}>{credit.creditedName ?? credit.artistName ?? credit.artistId}</Link>
    {showStatus && credit.identityStatus === 'unresolved_compound' && ' [unresolved compound]'}</>
}
function Pager({ page, total, onPage }: { page: number; total: number; onPage: (page: number) => void }) {
  return <Stack direction="row" spacing={2} sx={{ alignItems: 'center', my: 2 }}>
    <Button disabled={page <= 1} onClick={() => onPage(page - 1)}>Previous page</Button>
    <Typography>Page {page}; {total} results</Typography>
    <Button disabled={page * 25 >= total} onClick={() => onPage(page + 1)}>Next page</Button>
  </Stack>
}
function ArtistPage({ page, search, role, labelId, onPage }: {
  page: number; search: string; role: string; labelId?: string; onPage: (page: number) => void
}) {
  const [data, setData] = useState<GroupPage<CatalogArtist>>()
  const [error, setError] = useState('')
  const logout = useLogoutIfAccessDenied()
  useEffect(() => {
    const controller = new AbortController()
    const query = new URLSearchParams({ page: String(page), pageSize: '25', q: search })
    if (labelId) query.set('labelId', labelId)
    if (role) query.set('role', role)
    void artistClient.list(query, controller.signal).then(result => { if (!controller.signal.aborted) setData(result) }).catch(failure => {
      if (!controller.signal.aborted) { setError(failure instanceof Error ? failure.message : 'Artists could not be loaded.'); void logout(failure) }
    })
    return () => controller.abort()
  }, [page, search, role, labelId, logout])
  if (error) return <Alert severity="error">{error}</Alert>
  if (!data) return <LinearProgress aria-label="Loading catalog Artists" />
  return <>
    {!data.total && <Typography>No catalog Artists match this view.</Typography>}
    <Box sx={{ overflowX: 'auto' }}><Table aria-label={labelId ? 'Label artists' : 'Catalog artists'}>
      <TableHead><TableRow><TableCell>Artist</TableCell><TableCell>Kind</TableCell><TableCell>Identity status</TableCell><TableCell>Credited roles</TableCell></TableRow></TableHead>
      <TableBody>{data.items.map(artist => <TableRow key={artist.id} hover>
        <TableCell><Link to={`/artists/${encodeURIComponent(artist.id)}/show`}>{artist.name ?? 'Unnamed Artist'}</Link></TableCell>
        <TableCell>{artist.kind.replaceAll('_', ' ')}</TableCell>
        <TableCell>{artist.identityStatus.replaceAll('_', ' ')}</TableCell>
        <TableCell>{artist.roles.join(', ') || 'No credits recorded'}</TableCell>
      </TableRow>)}</TableBody>
    </Table></Box>
    <Pager page={page} total={data.total} onPage={onPage} />
  </>
}
function RoleFilter({ value, onChange }: { value: string; onChange: (value: string) => void }) {
  return <TextField select label="Credited role" value={value} onChange={event => onChange(event.target.value)} sx={{ minWidth: 180 }}>
    <MenuItem value="">All roles</MenuItem><MenuItem value="primary">Primary artist</MenuItem><MenuItem value="remixer">Remixer</MenuItem>
  </TextField>
}
export function LabelArtists({ labelId }: { labelId: string }) {
  const [search, setSearch] = useState('')
  const [role, setRole] = useState('')
  const [page, setPage] = useState(1)
  return <Stack spacing={2}>
    <Typography color="text.secondary">Artists credited on releases linked to this Label, including placement and Recording credits. Credits do not establish that an Artist is signed to the Label.</Typography>
    <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2}>
      <TextField label="Search label artists" value={search} onChange={e => { setSearch(e.target.value); setPage(1) }}
        slotProps={{ htmlInput: { maxLength: 200 } }} sx={{ flex: 1 }} />
      <RoleFilter value={role} onChange={value => { setRole(value); setPage(1) }} />
    </Stack>
    <ArtistPage key={`${labelId}:${page}:${search}:${role}`} {...{ labelId, page, search, role }} onPage={setPage} />
  </Stack>
}
export function ArtistList() {
  const { isPending } = useAuthenticated()
  const [params, setParams] = useSearchParams()
  const search = params.get('q') ?? ''
  const role = params.get('role') ?? ''
  const value = Number(params.get('page') ?? 1)
  const page = Number.isSafeInteger(value) && value > 0 ? value : 1
  function change(field: string, value: string) {
    const next = new URLSearchParams(params); next.set(field, value)
    if (field !== 'page') next.set('page', '1')
    setParams(next, { replace: true })
  }
  return <Stack spacing={2} sx={{ p: 3 }}>
    <Title title="Artists" /><Typography component="h1" variant="h4">Artists</Typography>
    <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2}>
      <TextField label="Search artists" value={search} onChange={e => change('q', e.target.value)} slotProps={{ htmlInput: { maxLength: 200 } }} sx={{ flex: 1 }} />
      <RoleFilter value={role} onChange={value => change('role', value)} />
    </Stack>
    {!isPending && <ArtistPage key={`${page}:${search}:${role}`} {...{ page, search, role }} onPage={p => change('page', String(p))} />}
  </Stack>
}
function Appearances({ id, tracks, page, onPage }: { id: string; tracks: boolean; page: number; onPage: (page: number) => void }) {
  const [data, setData] = useState<GroupPage<ArtistAppearance>>()
  const [error, setError] = useState('')
  const logout = useLogoutIfAccessDenied()
  useEffect(() => {
    const controller = new AbortController()
    void artistClient.credits(id, tracks, page, controller.signal).then(result => { if (!controller.signal.aborted) setData(result) }).catch(failure => {
      if (!controller.signal.aborted) { setError(failure instanceof Error ? failure.message : 'Artist credits could not be loaded.'); void logout(failure) }
    })
    return () => controller.abort()
  }, [id, tracks, page, logout])
  if (error) return <Alert severity="error">{error}</Alert>
  if (!data) return <LinearProgress aria-label="Loading Artist credits" />
  return <>
    {!data.total && <Typography>No {tracks ? 'track' : 'release'} credits recorded.</Typography>}
    <Box sx={{ overflowX: 'auto' }}><Table aria-label={tracks ? 'Artist track credits' : 'Artist release credits'}>
      <TableHead><TableRow>{tracks && <TableCell>Track</TableCell>}<TableCell>Release</TableCell><TableCell>Role</TableCell><TableCell>Credit scope</TableCell><TableCell>Credited as</TableCell></TableRow></TableHead>
      <TableBody>{data.items.map((credit, index) => <TableRow key={`${credit.scope}:${credit.creditId}:${credit.placementId}:${index}`}>
        {tracks && <TableCell>{credit.trackTitle ?? 'Untitled Recording'}{credit.mixName && ` (${credit.mixName})`}</TableCell>}
        <TableCell>{credit.releaseId ? <Link to={`/releases/${encodeURIComponent(credit.releaseId)}/show`}>{credit.releaseTitle ?? 'Untitled release'}</Link> : 'No release placement'}</TableCell>
        <TableCell>{credit.role}</TableCell><TableCell>{credit.scope}</TableCell><TableCell>{credit.creditedName ?? 'Canonical name'}</TableCell>
      </TableRow>)}</TableBody>
    </Table></Box>
    <Pager page={page} total={data.total} onPage={onPage} />
  </>
}
function Detail({ id }: { id: string }) {
  const [artist, setArtist] = useState<ArtistDetail>()
  const [error, setError] = useState('')
  const [releasePage, setReleasePage] = useState(1)
  const [trackPage, setTrackPage] = useState(1)
  const logout = useLogoutIfAccessDenied()
  useEffect(() => {
    const controller = new AbortController()
    void artistClient.get(id, controller.signal).then(result => { if (!controller.signal.aborted) setArtist(result) }).catch(failure => {
      if (!controller.signal.aborted) { setError(failure instanceof Error ? failure.message : 'Artist could not be loaded.'); void logout(failure) }
    })
    return () => controller.abort()
  }, [id, logout])
  if (error) return <Alert severity="error">{error}</Alert>
  if (!artist) return <LinearProgress aria-label="Loading catalog Artist" />
  return <Stack spacing={3}>
    <Title title={artist.name ?? 'Artist'} />
    <Typography component="h1" variant="h3">{artist.name ?? 'Unnamed Artist'}</Typography>
    <Stack direction="row" spacing={1}><Chip label={`Kind: ${artist.kind.replaceAll('_', ' ')}`} /><Chip label={`Identity: ${artist.identityStatus.replaceAll('_', ' ')}`} /></Stack>
    {artist.identityStatus === 'unresolved_compound' && <Alert severity="warning">This is an unresolved compound source identity. It has not been automatically split into individual Artists.</Alert>}
    <Typography color="text.secondary">Read-only catalog credits. These do not establish label affiliations, purchases or media availability. Release, placement and Recording scopes remain distinct.</Typography>
    <Typography component="h2" variant="h6">Release appearances and credits</Typography>
    <Appearances key={`release:${releasePage}`} id={id} tracks={false} page={releasePage} onPage={setReleasePage} />
    <Typography component="h2" variant="h6">Track credits</Typography>
    <Appearances key={`tracks:${trackPage}`} id={id} tracks page={trackPage} onPage={setTrackPage} />
    <Accordion><AccordionSummary expandIcon={<ExpandMoreIcon />}><Typography>Details and provider identities</Typography></AccordionSummary>
      <AccordionDetails>
        <Typography sx={{ overflowWrap: 'anywhere' }}>Catalog ID: {artist.id}</Typography>
        <Typography>Sort name: {artist.sortName ?? 'Not recorded'}</Typography>
        <Typography>Aliases: {artist.aliases.join(', ') || 'None recorded'}</Typography>
        {artist.externalReferences.map(r => <Typography key={`${r.source}:${r.externalId}`}>{r.source}: {r.externalId}{r.isPrimary ? ' (primary)' : ''}</Typography>)}
      </AccordionDetails>
    </Accordion>
  </Stack>
}
export function ArtistShow() {
  const { id } = useParams()
  const { isPending } = useAuthenticated()
  return <Box sx={{ p: 3 }}><Link to="/artists">All artists</Link>{!isPending && id && <Detail key={id} id={id} />}</Box>
}
