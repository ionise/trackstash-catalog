import { useEffect, useState } from 'react'
import { Accordion, AccordionDetails, AccordionSummary, Alert, Box, Button, ButtonBase, Chip, Divider, IconButton, SvgIcon, Tooltip, LinearProgress, Stack, Table, TableBody, TableCell, TableHead, TableRow, TextField, Typography } from '@mui/material'
import AlbumIcon from '@mui/icons-material/Album'
import CameraAltIcon from '@mui/icons-material/CameraAlt'
import ExpandMoreIcon from '@mui/icons-material/ExpandMore'
import { Title, useAuthenticated, useLogoutIfAccessDenied } from 'react-admin'
import { Link, useParams, useSearchParams } from 'react-router-dom'
import { releaseClient } from './catalog'
import type { CatalogRelease, GroupPage, ReleaseCredit, ReleaseDetail, ReleaseTrack } from './catalog'
import { ArtworkImage, ArtworkPanel } from './LabelArtwork'
import { ArtistCreditName } from './Artists'
import { siBeatport } from 'simple-icons'
import { releaseLinks, linkPresentation, trackContributors } from './labelPresentation'
import { TrackTitleArtists } from './TrackPresentation'

function Pager({ page, total, onPage }: { page: number; total: number; onPage: (page: number) => void }) {
  return <Stack direction="row" spacing={2} useFlexGap sx={{ alignItems: 'center', my: 2, flexWrap: 'wrap' }}>
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
        <TableHead><TableRow><TableCell>Release</TableCell><TableCell>{labelId ? 'Primary artist' : 'Label'}</TableCell><TableCell>Catalog number</TableCell><TableCell>Publish date</TableCell><TableCell>Tracks</TableCell></TableRow></TableHead>
        <TableBody>{data.items.map(release => <TableRow key={release.id} hover>
          <TableCell><Stack direction="row" spacing={1.5} sx={{ alignItems: 'center' }}>
            {release.coverAssetId
              ? <Link aria-label={`View ${release.title ?? 'untitled release'}`} to={`/releases/${encodeURIComponent(release.id)}/show`}>
                <ArtworkImage assetId={release.coverAssetId} caption={`${release.title ?? 'Release'} cover thumbnail`} size={40} />
              </Link>
              : <Box sx={{ width: 40, height: 40, flexShrink: 0, display: 'flex', alignItems: 'center', justifyContent: 'center', bgcolor: 'action.hover', borderRadius: 1 }}><AlbumIcon color="disabled" /></Box>}
            <Link to={`/releases/${encodeURIComponent(release.id)}/show`}>{release.title ?? 'Untitled release'}</Link>
          </Stack></TableCell>
          <TableCell>{labelId
            ? release.primaryArtists.length ? release.primaryArtists.map((credit, index) =>
              <span key={`${credit.artistId}:${index}`}>{index > 0 && ', '}<ArtistCreditName credit={credit} /></span>) : 'Not recorded'
            : release.labelId ? <Link to={`/labels/${encodeURIComponent(release.labelId)}/show`}>{release.labelName ?? release.labelId}</Link> : 'Not recorded'}</TableCell>
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
    <ArtistCreditName credit={credit} showStatus={false} /> - {credit.role}
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
    <Box sx={{ overflowX: 'auto', width: '100%', minWidth: 0, contain: 'inline-size' }}><Table aria-label="Catalog release tracks">
      <TableHead><TableRow><TableCell>#</TableCell><TableCell>Title / Artist · Remixer</TableCell><TableCell>Duration</TableCell></TableRow></TableHead>
      <TableBody>{data.items.map((track, index) => <TableRow key={track.placementId}>
        <TableCell>{(data.page - 1) * data.pageSize + index + 1}</TableCell>
        <TableCell>
          <TrackTitleArtists title={track.title} mixName={track.mixName} credits={trackContributors(track.credits, track.recordingCredits)} />
          <Accordion disableGutters elevation={0} sx={{ bgcolor: 'transparent' }}><AccordionSummary expandIcon={<ExpandMoreIcon />}><Typography variant="caption">Track details</Typography></AccordionSummary>
            <AccordionDetails>
              <Typography variant="caption" sx={{ overflowWrap: 'anywhere' }}>ISRC: {track.isrc ?? 'unknown'}<br />Recording ID: {track.recordingId}<br />Placement ID: {track.placementId}<br />
                Catalog display order: {track.displayOrder} ({track.orderSource})<br />Source position: {track.sourcePosition ?? 'unknown'}<br />Official disc/track: {track.discNumber ?? '?'}/{track.trackNumber ?? '?'}</Typography>
              <Typography variant="subtitle2">Track listing credits</Typography><Credits items={track.credits} />
              <Typography variant="subtitle2">Recording credits</Typography><Credits items={track.recordingCredits} />
              {track.externalReferences.map(r => <Typography variant="caption" sx={{ display: 'block' }} key={`${r.source}:${r.externalId}`}>{r.source}: {r.externalId}</Typography>)}
            </AccordionDetails>
          </Accordion>
        </TableCell>
        <TableCell>{track.durationMs === null ? 'Unknown' : `${Math.floor(track.durationMs / 60000)}:${String(Math.floor(track.durationMs / 1000) % 60).padStart(2, '0')}`}</TableCell>
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
  return <ArtworkPanel key={data.id} id={data.id} name={data.title} version={data.version} release>{(artwork, gallery) => {
    const preferredCover = artwork.gallery?.items.find(item => item.isPreferred && item.role === 'cover')
    return <Stack spacing={3} sx={{ mt: 2, minWidth: 0 }}>
    <Title title={data.title ?? 'Release'} />
    <Stack component="header" direction={{ xs: 'column', sm: 'row' }} spacing={3} sx={{ alignItems: 'flex-start' }}>
      <Box sx={{ width: 240, maxWidth: '100%', flexShrink: 0 }}>
        <Tooltip title="Change release cover and gallery"><span><ButtonBase aria-label="Change release artwork" disabled={artwork.busy} onClick={artwork.manage}
          sx={{ position: 'relative', width: 240, maxWidth: '100%', borderRadius: 2, overflow: 'hidden',
            '& .cover-action': { opacity: 0 },
            '&:hover .cover-action, &.Mui-focusVisible .cover-action': { opacity: 1 },
            '&.Mui-focusVisible': { outline: '2px solid', outlineColor: 'primary.main', outlineOffset: 2 },
            '@media (hover: none)': { '& .cover-action': { opacity: 1 } } }}>
        {preferredCover ?
          <ArtworkImage assetId={preferredCover.assetId} caption={`${data.title ?? 'Release'} cover`} size={240} />
          : <Stack sx={{ width: 240, maxWidth: '100%', height: 240, bgcolor: 'action.hover', borderRadius: 2, justifyContent: 'center', alignItems: 'center' }} spacing={1}>
            <AlbumIcon sx={{ fontSize: 80 }} color="disabled" />
            <Typography color="text.secondary">{artwork.error ? 'Cover unavailable' : artwork.gallery ? 'No preferred cover' : 'Loading cover...'}</Typography>
          </Stack>}
          <Box className="cover-action" sx={{ position: 'absolute', right: 8, bottom: 8, p: 1,
            display: 'flex', borderRadius: '50%', bgcolor: 'background.paper', color: 'text.primary', boxShadow: 2 }}>
            <CameraAltIcon />
          </Box>
        </ButtonBase></span></Tooltip>
        {artwork.error && <Alert severity="warning">{artwork.error}</Alert>}
      </Box>
      <Stack spacing={2} sx={{ minWidth: 0, maxWidth: '100%' }}>
        <Chip label="Release" variant="outlined" size="small" sx={{ alignSelf: 'flex-start' }} />
        <Typography component="h1" variant="h3" sx={{ fontWeight: 600, overflowWrap: 'anywhere' }}>{data.title ?? 'Untitled release'}</Typography>
        <Stack direction="row" spacing={1}>{releaseLinks(data).map(link => <Tooltip key={link.url} title={`Open ${linkPresentation(link).label} in a new tab`}>
          <IconButton component="a" href={link.url} target="_blank" rel="noopener noreferrer" aria-label={linkPresentation(link).label}>
            <SvgIcon viewBox="0 0 24 24"><path d={siBeatport.path} /></SvgIcon>
          </IconButton>
        </Tooltip>)}</Stack>
        <Stack direction="row" useFlexGap spacing={1} sx={{ flexWrap: 'wrap' }}>{data.labels.map(label =>
          <Link key={label.id} to={`/labels/${encodeURIComponent(label.id)}/show`}>{label.name ?? label.id}{label.isPrimary ? ' (primary label)' : ''}</Link>)}</Stack>
        <Typography>Catalog number: {data.catalogNumber ?? 'Unknown'}; UPC: {data.upc ?? 'Unknown'}</Typography>
        <Typography>Publish date: {data.publishDate ?? 'Unknown'}; new release date: {data.newReleaseDate ?? 'Unknown'}</Typography>
        <Box><Typography component="h2" variant="h6">Release credits</Typography><Credits items={data.credits} /></Box>
      </Stack>
    </Stack>
    <Divider />
    <Typography component="h2" variant="h6">Tracks</Typography>
    <Tracks key={`${id}:${page}`} id={id} page={page} onPage={setPage} />
    {gallery}
    <Accordion><AccordionSummary expandIcon={<ExpandMoreIcon />}><Typography>Details and provider identities</Typography></AccordionSummary>
      <AccordionDetails>
        <Typography sx={{ overflowWrap: 'anywhere' }}>Catalog ID: {data.id}</Typography>
        {data.externalReferences.map(r => <Typography key={`${r.source}:${r.externalId}`}>{r.source}: {r.externalId}{r.isPrimary ? ' (primary)' : ''}</Typography>)}
        <Typography variant="body2" color="text.secondary" sx={{ mt: 2 }}>Catalog metadata only. Track placements do not establish purchases, media availability or NAS backup coverage.</Typography>
      </AccordionDetails>
    </Accordion>
  </Stack>
  }}</ArtworkPanel>
}
export function ReleaseShow() {
  const { id } = useParams()
  const { isPending } = useAuthenticated()
  return <Box sx={{ p: 3, minWidth: 0, width: '100%' }}><Link to="/releases">All releases</Link>
    {!isPending && id && <Detail key={id} id={id} />}
  </Box>
}
