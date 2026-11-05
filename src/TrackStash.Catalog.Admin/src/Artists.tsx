import { useEffect, useState } from 'react'
import { Accordion, AccordionDetails, AccordionSummary, Alert, Box, Button, ButtonBase, Chip, IconButton, SvgIcon, Tooltip, LinearProgress, MenuItem, Stack, Tab, Tabs,
  Table, TableBody, TableCell, TableHead, TableRow, TextField, Typography } from '@mui/material'
import ExpandMoreIcon from '@mui/icons-material/ExpandMore'
import CameraAltIcon from '@mui/icons-material/CameraAlt'
import PersonIcon from '@mui/icons-material/Person'
import FavoriteIcon from '@mui/icons-material/Favorite'
import FavoriteBorderIcon from '@mui/icons-material/FavoriteBorder'
import EditIcon from '@mui/icons-material/Edit'
import AlbumIcon from '@mui/icons-material/Album'
import MusicNoteIcon from '@mui/icons-material/MusicNote'
import QueueMusicIcon from '@mui/icons-material/QueueMusic'
import LanguageIcon from '@mui/icons-material/Language'
import { siBeatport } from 'simple-icons'
import { Title, useAuthenticated, useLogoutIfAccessDenied } from 'react-admin'
import { Link, useParams, useSearchParams } from 'react-router-dom'
import { artistClient } from './catalog'
import type { GroupedArtistAppearance, ArtistDetail, CatalogArtist, GroupPage, ReleaseCredit } from './catalog'
import { ArtistProfilePanel } from './ArtistProfile'
import { ArtworkImage, ArtworkPanel } from './LabelArtwork'
import { artistLinks, linkPresentation } from './labelPresentation'

export function ArtistCreditName({ credit, showStatus = true }: { credit: ReleaseCredit; showStatus?: boolean }) {
  return <><Link to={`/artists/${encodeURIComponent(credit.artistId)}/show`}>{credit.creditedName ?? credit.artistName ?? credit.artistId}</Link>
    {showStatus && credit.identityStatus === 'unresolved_compound' && ' [unresolved compound]'}</>
}
function Pager({ page, total, onPage }: { page: number; total: number; onPage: (page: number) => void }) {
  return <Stack direction="row" spacing={2} useFlexGap sx={{ alignItems: 'center', my: 2, flexWrap: 'wrap' }}>
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
    <Box sx={{ overflowX: 'auto', width: '100%', minWidth: 0, contain: 'inline-size' }}><Table aria-label={labelId ? 'Label artists' : 'Catalog artists'}>
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
function CreditEvidence({ credits }: { credits: GroupedArtistAppearance['credits'] }) {
  const scopes = [
    { scope: 'release', label: 'Release credit: credited on the whole release; does not imply credit on every track.', icon: <AlbumIcon fontSize="small" /> },
    { scope: 'recording', label: 'Recording credit: credited on this recording, across its release appearances.', icon: <MusicNoteIcon fontSize="small" /> },
    { scope: 'placement', label: 'Track listing credit: credited on this track as listed on this release.', icon: <QueueMusicIcon fontSize="small" /> },
  ]
  return <Stack direction="row" spacing={0.5}>{scopes.filter(s => credits.some(c => c.scope === s.scope)).map(s => {
    const hint = `${s.label} ${credits.filter(c => c.scope === s.scope).map(c => `${c.role}${c.creditedName ? ` as ${c.creditedName}` : ' under canonical name'}`).join('; ')}`
    return <Tooltip key={s.scope} title={hint}><Box component="span" tabIndex={0} aria-label={hint} sx={{ display: 'inline-flex' }}>{s.icon}</Box></Tooltip>
  })}</Stack>
}
function Appearances({ id, tracks, page, onPage }: { id: string; tracks: boolean; page: number; onPage: (page: number) => void }) {
  const [data, setData] = useState<GroupPage<GroupedArtistAppearance>>()
  const [error, setError] = useState('')
  const logout = useLogoutIfAccessDenied()
  useEffect(() => {
    const controller = new AbortController()
    void artistClient.appearances(id, tracks, page, controller.signal).then(result => { if (!controller.signal.aborted) setData(result) }).catch(failure => {
      if (!controller.signal.aborted) { setError(failure instanceof Error ? failure.message : 'Artist credits could not be loaded.'); void logout(failure) }
    })
    return () => controller.abort()
  }, [id, tracks, page, logout])
  if (error) return <Alert severity="error">{error}</Alert>
  if (!data) return <LinearProgress aria-label="Loading Artist credits" />
  return <>
    {!data.total && <Typography>No {tracks ? 'track' : 'release'} credits recorded.</Typography>}
    <Box sx={{ overflowX: 'auto', width: '100%', minWidth: 0, contain: 'inline-size' }}><Table aria-label={tracks ? 'Artist track credits' : 'Artist release credits'}>
      <TableHead><TableRow>{tracks && <TableCell>Track</TableCell>}<TableCell>Release</TableCell>{!tracks && <TableCell>Label</TableCell>}<TableCell>Role</TableCell><TableCell>Credit evidence</TableCell><TableCell>Credited as</TableCell></TableRow></TableHead>
      <TableBody>{data.items.map(credit => <TableRow key={tracks ? credit.placementId ?? `recording:${credit.recordingId}` : credit.releaseId}>
        {tracks && <TableCell>{credit.trackTitle ?? 'Untitled Recording'}{credit.mixName && ` (${credit.mixName})`}</TableCell>}
        <TableCell><Stack direction="row" spacing={1} sx={{ alignItems: 'center' }}>
          {!tracks && (credit.coverAssetId ? <ArtworkImage assetId={credit.coverAssetId} caption={`${credit.releaseTitle ?? 'Release'} cover thumbnail`} size={40} />
            : <Box sx={{ width: 40, height: 40, flexShrink: 0, bgcolor: 'action.hover', display: 'flex', alignItems: 'center', justifyContent: 'center' }}><AlbumIcon color="disabled" /></Box>)}
          {credit.releaseId ? <Tooltip describeChild title={`View release: ${credit.releaseTitle ?? 'Untitled release'}`}><Link to={`/releases/${encodeURIComponent(credit.releaseId)}/show`}>{credit.releaseTitle ?? 'Untitled release'}</Link></Tooltip> : 'No release placement'}
        </Stack></TableCell>
        {!tracks && <TableCell>{credit.labels.length ? <Stack>{credit.labels.map(label => <Tooltip describeChild key={label.id} title={`View Label: ${label.name ?? 'Unnamed Label'}${label.isPrimary ? ' (primary label)' : ''}`}>
          <Link to={`/labels/${encodeURIComponent(label.id)}/show`}>{label.name ?? 'Unnamed Label'}</Link>
        </Tooltip>)}</Stack> : 'Not recorded'}</TableCell>}
        <TableCell>{[...new Set(credit.credits.map(c => c.role))].join(', ')}</TableCell><TableCell><CreditEvidence credits={credit.credits} /></TableCell>
        <TableCell>{[...new Set(credit.credits.map(c => c.creditedName ?? 'Canonical name'))].join(', ')}</TableCell>
      </TableRow>)}</TableBody>
    </Table></Box>
    <Pager page={page} total={data.total} onPage={onPage} />
  </>
}
function Detail({ id }: { id: string }) {
  const [artist, setArtist] = useState<ArtistDetail>()
  const [error, setError] = useState('')
  const [params, setParams] = useSearchParams()
  const sections = ['Featured', 'Tracks', 'Releases', 'Associations', 'Details']
  const selected = params.get('tab') ?? 'Featured'
  const tab = sections.includes(selected) ? selected : 'Featured'
  function pageValue(key: string) {
    const number = Number(params.get(key) ?? 1)
    return Number.isSafeInteger(number) && number > 0 ? number : 1
  }
  const releasePage = pageValue('releasePage')
  const trackPage = pageValue('trackPage')
  function change(key: string, value: string) {
    const next = new URLSearchParams(params); next.set(key, value); setParams(next, { replace: true })
  }
  const [bioExpanded, setBioExpanded] = useState(false)
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
  return <ArtistProfilePanel id={id} version={artist.version}>{(profile, profileDialog) =>
    <ArtworkPanel id={id} name={artist.name} version={profile.snapshot?.etag ?? artist.version} artist onChanged={profile.reload}>{(artwork, gallery) => {
      const portrait = artwork.gallery?.items.find(item => item.isPreferred && item.role === 'portrait')
      const biography = profile.snapshot?.profile.biography
      return <Stack spacing={3} sx={{ mt: 2 }}>
    <Title title={artist.name ?? 'Artist'} />
    <Stack component="header" direction={{ xs: 'column', sm: 'row' }} spacing={3} sx={{ alignItems: 'flex-start' }}>
      <Box sx={{ width: 240, maxWidth: '100%', flexShrink: 0 }}>
        <Tooltip title="Change Artist portrait and gallery"><span><ButtonBase aria-label="Change Artist artwork" disabled={artwork.busy} onClick={artwork.manage}
          sx={{ width: 240, maxWidth: '100%', height: 240, position: 'relative', borderRadius: 2, overflow: 'hidden',
            '& .portrait-action': { opacity: 0 }, '&:hover .portrait-action, &.Mui-focusVisible .portrait-action': { opacity: 1 },
            '&.Mui-focusVisible': { outline: '2px solid', outlineColor: 'primary.main', outlineOffset: 2 },
            '@media (hover: none)': { '& .portrait-action': { opacity: 1 } } }}>
          {portrait ? <ArtworkImage assetId={portrait.assetId} caption={`${artist.name ?? 'Artist'} portrait`} size={240} />
            : <Stack sx={{ width: '100%', height: '100%', bgcolor: 'action.hover', alignItems: 'center', justifyContent: 'center' }}>
              <PersonIcon color="disabled" sx={{ fontSize: 72 }} /><Typography>{artwork.gallery ? 'No preferred portrait' : artwork.error ? 'Portrait unavailable' : 'Loading portrait...'}</Typography>
            </Stack>}
          <Box className="portrait-action" sx={{ position: 'absolute', right: 8, bottom: 8, p: 1, display: 'flex', borderRadius: '50%', bgcolor: 'background.paper', color: 'text.primary', boxShadow: 2 }}><CameraAltIcon /></Box>
        </ButtonBase></span></Tooltip>
      </Box>
      <Stack spacing={2} sx={{ minWidth: 0, maxWidth: '100%' }}>
        <Typography component="h1" variant="h3" sx={{ overflowWrap: 'anywhere' }}>{artist.name ?? 'Unnamed Artist'}</Typography>
        <Stack direction="row" useFlexGap spacing={1} sx={{ flexWrap: 'wrap' }}>
          <Chip label={`Kind: ${artist.kind.replaceAll('_', ' ')}`} /><Chip label={`Identity: ${artist.identityStatus.replaceAll('_', ' ')}`} />
          <Tooltip title={profile.snapshot?.profile.favourite ? 'Remove Artist from favourites' : 'Add Artist to favourites'}><span>
            <IconButton aria-label="Favourite" aria-pressed={profile.snapshot?.profile.favourite ?? false} disabled={!profile.snapshot || profile.busy || Boolean(profile.error)}
              color={profile.snapshot?.profile.favourite ? 'primary' : 'default'} onClick={profile.toggleFavourite}>
              {profile.snapshot?.profile.favourite ? <FavoriteIcon /> : <FavoriteBorderIcon />}
            </IconButton>
          </span></Tooltip>
        </Stack>
        <Stack direction="row" useFlexGap spacing={2} sx={{ flexWrap: 'wrap' }}>{artistLinks(artist, profile.snapshot?.profile).map(link =>
          <Tooltip key={link.url} title={`Open ${linkPresentation(link).label} in a new tab`}>
            <IconButton component="a" href={link.url} target="_blank" rel="noopener noreferrer" aria-label={linkPresentation(link).label}>
              {linkPresentation(link).service === 'Beatport' ? <SvgIcon viewBox="0 0 24 24"><path d={siBeatport.path} /></SvgIcon> : <LanguageIcon />}
            </IconButton>
          </Tooltip>)}</Stack>
        <Tooltip title="Edit Artist links, private notes and review information"><span>
          <Button disabled={!profile.snapshot || profile.busy} onClick={profile.edit}>Edit Artist profile</Button>
        </span></Tooltip>
      </Stack>
    </Stack>
    {artist.identityStatus === 'unresolved_compound' && <Alert severity="warning">This is an unresolved compound source identity. It has not been automatically split into individual Artists.</Alert>}
    {profile.error && <Alert severity="warning" action={<Button disabled={profile.busy} onClick={profile.reload}>Reload Artist profile</Button>}>{profile.error}</Alert>}
    {artwork.error && <Alert severity="warning">{artwork.error}</Alert>}
    <Tabs value={tab} onChange={(_, value: string) => change('tab', value)} variant="scrollable" scrollButtons="auto" allowScrollButtonsMobile aria-label="Artist sections"
      sx={{ minWidth: 0, maxWidth: '100%', width: '100%', contain: 'inline-size' }}>
      {sections.map(section => <Tab key={section} label={section} value={section} id={`artist-tab-${section}`} aria-controls={`artist-panel-${section}`} />)}
    </Tabs>
    {sections.map(section => <Box key={section} role="tabpanel" id={`artist-panel-${section}`} aria-labelledby={`artist-tab-${section}`} hidden={tab !== section}>
      {section === 'Featured' && tab === section && <Stack spacing={2}>
        <Stack direction="row" spacing={1} sx={{ alignItems: 'center' }}>
          <Typography component="h2" variant="h6">Biography</Typography>
          <Tooltip title="Edit biography and its source"><span><IconButton aria-label="Edit biography" disabled={!profile.snapshot || profile.busy} onClick={profile.editBiography}><EditIcon fontSize="small" /></IconButton></span></Tooltip>
        </Stack>
        <Typography sx={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}>{biography ? bioExpanded || biography.length <= 700 ? biography : `${biography.slice(0, 700)}...` : profile.snapshot ? 'No biography recorded.' : profile.busy ? 'Loading biography...' : 'Biography unavailable.'}</Typography>
        {biography && biography.length > 700 && <Button onClick={() => setBioExpanded(value => !value)}>{bioExpanded ? 'Show less biography' : 'Read full biography'}</Button>}
        {profile.snapshot?.profile.biographySourceUrl && <Tooltip describeChild title="Open biography source in a new tab"><Box component="a" href={profile.snapshot.profile.biographySourceUrl} target="_blank" rel="noopener noreferrer">Biography source</Box></Tooltip>}
        <Typography component="h2" variant="h6">Latest releases</Typography>
        <Typography color="text.secondary">Latest-release cards and newest-first ordering are planned for the next increment. Browse recorded appearances in Releases.</Typography>
      </Stack>}
      {section === 'Tracks' && tab === section && <Stack spacing={2}>
        <Typography component="h2" variant="h6">Track credits</Typography>
        <Typography color="text.secondary">One row per credited track listing; repeated appearances on different releases remain separate. Credit icons explain the underlying evidence. Ordered by release title.</Typography>
        <Appearances key={`tracks:${trackPage}`} id={id} tracks page={trackPage} onPage={page => change('trackPage', String(page))} />
      </Stack>}
      {section === 'Releases' && tab === section && <Stack spacing={2}>
        <Typography component="h2" variant="h6">Release appearances and credits</Typography>
        <Typography color="text.secondary">One row per release, with combined roles and credit evidence, artwork and linked Labels. Ordered by release title.</Typography>
        <Appearances key={`release:${releasePage}`} id={id} tracks={false} page={releasePage} onPage={page => change('releasePage', String(page))} />
      </Stack>}
      {section === 'Associations' && tab === section && <Stack spacing={3}>
        <Typography color="text.secondary">Planned catalog-credit summaries, not signing or personal relationship claims. No association counts have been calculated yet.</Typography>
        <Box><Typography component="h2" variant="h6">Labels appeared on</Typography><Typography>Planned: distinct releases, credited Recordings and placement appearances per Label.</Typography></Box>
        <Stack direction={{ xs: 'column', md: 'row' }} spacing={3}>{['Remixed for', 'Remixed by', 'Collaborated with'].map(title =>
          <Box key={title} sx={{ flex: 1 }}><Typography component="h2" variant="h6">{title}</Typography><Typography>Not implemented yet. Requires supporting same-track credit evidence.</Typography></Box>)}</Stack>
      </Stack>}
      {section === 'Details' && tab === section && <Stack spacing={3}>
        <Typography component="h2" variant="h6">Personal notes and review</Typography>
        <Typography sx={{ whiteSpace: 'pre-wrap' }}>{profile.snapshot ? profile.snapshot.profile.notes ?? 'No notes recorded.' : 'Notes unavailable.'}</Typography>
        <Typography>Review: {profile.snapshot?.profile.reviewStatus.replaceAll('_', ' ') ?? 'Unavailable'}</Typography>
        {gallery}
    <Accordion><AccordionSummary expandIcon={<ExpandMoreIcon />}><Typography>Details and provider identities</Typography></AccordionSummary>
      <AccordionDetails>
        <Typography sx={{ overflowWrap: 'anywhere' }}>Catalog ID: {artist.id}</Typography>
        <Typography>Sort name: {artist.sortName ?? 'Not recorded'}</Typography>
        <Typography>Aliases: {artist.aliases.join(', ') || 'None recorded'}</Typography>
        {artist.externalReferences.map(r => <Typography key={`${r.source}:${r.externalId}`}>{r.source}: {r.externalId}{r.isPrimary ? ' (primary)' : ''}</Typography>)}
      </AccordionDetails>
    </Accordion>
      </Stack>}
    </Box>)}
    {profileDialog}
  </Stack>
    }}</ArtworkPanel>
  }</ArtistProfilePanel>
}
export function ArtistShow() {
  const { id } = useParams()
  const { isPending } = useAuthenticated()
  return <Box sx={{ p: 3, minWidth: 0, width: '100%' }}><Link to="/artists">All artists</Link>{!isPending && id && <Detail key={id} id={id} />}</Box>
}
