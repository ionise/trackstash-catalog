import { useEffect, useState } from 'react'
import { Accordion, AccordionDetails, AccordionSummary, Alert, Box, Chip, Divider, IconButton, LinearProgress, Stack, SvgIcon, Tooltip, Typography } from '@mui/material'
import ExpandMoreIcon from '@mui/icons-material/ExpandMore'
import AlbumIcon from '@mui/icons-material/Album'
import { siBeatport } from 'simple-icons'
import { Title, useAuthenticated, useLogoutIfAccessDenied } from 'react-admin'
import { Link, useParams, useSearchParams } from 'react-router-dom'
import { trackClient } from './catalog'
import type { CatalogTrack, RecordingDetail } from './catalog'
import { ArtworkImage } from './LabelArtwork'
import { linkPresentation, recordingLinks } from './labelPresentation'
import { TrackArtists } from './TrackPresentation'
import { LocalTrackList, TrackList } from './TrackList'
import { formatDuration, trackQuery, useUrlTrackView } from './trackView'
import { RecordingEnrichment } from './RecordingEnrichment'
import { RecordingMedia } from './Media'

export function TrackListPage() {
  const { isPending } = useAuthenticated()
  const [view, setView] = useUrlTrackView()
  return <Stack spacing={2} sx={{ p: 3, minWidth: 0 }}>
    <Title title="Tracks" /><Typography component="h1" variant="h4">Tracks</Typography>
    <Typography color="text.secondary">One row per Recording (a distinct piece of audio). A Recording released more than once shows its newest release and a release count; open it to see every release. Catalog metadata only, independent of purchases or media files.</Typography>
    {!isPending && <TrackList filter={{}} context="all" view={view} onChange={setView} label="Catalog tracks" groupByRecording />}
  </Stack>
}

function RecordingArtwork({ id, releaseId }: { id: string; releaseId?: string }) {
  const [track, setTrack] = useState<CatalogTrack>()
  const [loaded, setLoaded] = useState(false)
  const [error, setError] = useState('')
  const logout = useLogoutIfAccessDenied()
  useEffect(() => {
    const controller = new AbortController()
    const query = trackQuery({ recordingId: id, releaseId }, { page: 1, q: '', sort: 'date_desc' }, true)
    query.set('pageSize', '1')
    void trackClient.list(query, controller.signal).then(result => {
      if (!controller.signal.aborted) { setTrack(result.items[0]); setLoaded(true) }
    }).catch(failure => {
      if (!controller.signal.aborted) {
        setError(failure instanceof Error ? failure.message : 'Recording release artwork could not be loaded.')
        void logout(failure)
      }
    })
    return () => controller.abort()
  }, [id, releaseId, logout])
  return <Stack component="section" aria-label="Recording release artwork" spacing={1}
    sx={{ width: 240, maxWidth: '100%', flexShrink: 0 }}>
    {error ? <Alert severity="warning">{error}</Alert> : !loaded
      ? <LinearProgress aria-label="Loading Recording release artwork" />
      : <>
        {track?.coverAssetId && track.releaseId ? <Link to={`/releases/${encodeURIComponent(track.releaseId)}/show`}
          aria-label={`View artwork release: ${track.releaseTitle ?? 'Untitled release'}`} style={{ display: 'block' }}>
          <ArtworkImage assetId={track.coverAssetId} caption={`${track.releaseTitle ?? 'Release'} cover for this Recording`} size={240} />
        </Link> : <Stack spacing={1} sx={{ width: 240, maxWidth: '100%', height: 240, bgcolor: 'action.hover',
          borderRadius: 2, alignItems: 'center', justifyContent: 'center', textAlign: 'center', p: 2 }}>
          <AlbumIcon sx={{ fontSize: 80 }} color="disabled" />
          <Typography color="text.secondary">{releaseId && !track ? 'This Recording is not listed on the requested release.'
            : track?.releaseId ? 'No preferred release cover' : 'No release artwork available'}</Typography>
        </Stack>}
        {track?.releaseId && <Typography variant="body2">
          {releaseId ? 'Artwork from: ' : 'Newest release: '}
          <Link to={`/releases/${encodeURIComponent(track.releaseId)}/show`}>{track.releaseTitle ?? 'Untitled release'}</Link>
        </Typography>}
      </>}
  </Stack>
}

function Detail({ id }: { id: string }) {
  const [revision, setRevision] = useState(0)
  const [mediaExpanded, setMediaExpanded] = useState(false)
  const [params] = useSearchParams()
  const releaseId = params.get('releaseId') || undefined
  const [data, setData] = useState<RecordingDetail>()
  const [error, setError] = useState('')
  const logout = useLogoutIfAccessDenied()
  useEffect(() => {
    const controller = new AbortController()
    void trackClient.recording(id, controller.signal).then(result => { if (!controller.signal.aborted) setData(result) }).catch(failure => {
      if (!controller.signal.aborted) { setError(failure instanceof Error ? failure.message : 'Recording could not be loaded.'); void logout(failure) }
    })
    return () => controller.abort()
  }, [id, logout, revision])
  if (error) return <Alert severity="error">{error}</Alert>
  if (!data) return <LinearProgress aria-label="Loading catalog Recording" />
  return <Stack spacing={3} sx={{ mt: 2, minWidth: 0 }}>
    <Title title={data.title ?? 'Recording'} />
    <Stack component="header" direction={{ xs: 'column', sm: 'row' }} spacing={3} sx={{ alignItems: 'flex-start', minWidth: 0 }}>
      <RecordingArtwork key={`${id}:${releaseId ?? ''}:${revision}`} id={id} releaseId={releaseId} />
      <Stack spacing={1.5} sx={{ flex: 1, minWidth: 0, width: { xs: '100%', sm: 'auto' } }}>
        <Chip label="Recording" variant="outlined" size="small" sx={{ alignSelf: 'flex-start' }} />
        <Typography component="h1" variant="h4" sx={{ overflowWrap: 'anywhere' }}>{data.title ?? 'Untitled recording'}
          {data.mixName && <Typography component="span" variant="h5" color="text.secondary"> {data.mixName}</Typography>}</Typography>
        <TrackArtists credits={data.credits} />
        <Stack direction="row" spacing={1}>{recordingLinks(data).map(link => <Tooltip key={link.url} title={`Open ${linkPresentation(link).label} in a new tab`}>
          <IconButton component="a" href={link.url} target="_blank" rel="noopener noreferrer" aria-label={linkPresentation(link).label}>
            <SvgIcon viewBox="0 0 24 24"><path d={siBeatport.path} /></SvgIcon>
          </IconButton>
        </Tooltip>)}</Stack>
        <Typography>ISRC: {data.isrc ?? 'Unknown'}; length: {formatDuration(data.durationMs)}; releases: {data.releaseCount}</Typography>
        <Stack direction="row" spacing={1} sx={{ flexWrap: 'wrap', gap: 1 }}>
          <Chip label={`BPM: ${data.musicalMetadata?.bpm ?? 'Unknown'}`} />
          <Chip label={`Key: ${data.musicalMetadata?.keyValue ?? 'Unknown'}${data.musicalMetadata?.keyNotation ? ` (${data.musicalMetadata.keyNotation})` : ''}`} />
          <Tooltip title="Reviewed linked copies, not a live file-presence check"><Chip label={`${data.linkedCopyCount ?? 'Unknown'} linked copies`} /></Tooltip>
        </Stack>
        <Typography variant="body2" color="text.secondary">{[data.musicalMetadata?.genres?.join(', '), data.musicalMetadata?.subgenres?.join(', ')].filter(Boolean).join(' / ')}</Typography>
      </Stack>
    </Stack>
    <Divider />
    <Accordion expanded={mediaExpanded} onChange={(_, expanded) => setMediaExpanded(expanded)}>
      <AccordionSummary expandIcon={<ExpandMoreIcon />}><Typography>Media locations and file details — {data.linkedCopyCount ?? 'Unknown'} linked copies</Typography></AccordionSummary>
      <AccordionDetails>{mediaExpanded && <RecordingMedia key={`media:${id}:${revision}`} id={id} />}</AccordionDetails>
    </Accordion>
    <RecordingEnrichment key={id} id={id} onApplied={() => setRevision(value => value + 1)} />
    <Typography component="h2" variant="h6">Releases of this Recording</Typography>
    <LocalTrackList key={revision} filter={{ recordingId: id }} context="recording" label="Recording releases" />
    <Accordion><AccordionSummary expandIcon={<ExpandMoreIcon />}><Typography>Details and provider identities</Typography></AccordionSummary>
      <AccordionDetails>
        <Typography sx={{ overflowWrap: 'anywhere' }}>Catalog ID: {data.id}</Typography>
        {data.musicalMetadata?.preferredGenre && <Typography>Preferred classification: {data.musicalMetadata.preferredGenre}</Typography>}
        {data.externalReferences.map(r => <Typography key={`${r.source}:${r.externalId}`}>{r.source}: {r.externalId}{r.isPrimary ? ' (primary)' : ''}</Typography>)}
        <Typography variant="body2" color="text.secondary" sx={{ mt: 2 }}>A Recording is one piece of audio (title, mix and ISRC). Each release it appears on is a separate track listing; explicitly accepted media copies are listed above.</Typography>
      </AccordionDetails>
    </Accordion>
  </Stack>
}

export function RecordingShow() {
  const { id } = useParams()
  const { isPending } = useAuthenticated()
  return <Box sx={{ p: 3, minWidth: 0, width: '100%' }}><Link to="/tracks">All tracks</Link>
    {!isPending && id && <Detail key={id} id={id} />}
  </Box>
}
