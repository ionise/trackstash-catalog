import { useEffect, useState } from 'react'
import { Accordion, AccordionDetails, AccordionSummary, Alert, Box, Chip, Divider, IconButton, LinearProgress, Stack, SvgIcon, Tooltip, Typography } from '@mui/material'
import ExpandMoreIcon from '@mui/icons-material/ExpandMore'
import { siBeatport } from 'simple-icons'
import { Title, useAuthenticated, useLogoutIfAccessDenied } from 'react-admin'
import { Link, useParams } from 'react-router-dom'
import { trackClient } from './catalog'
import type { RecordingDetail } from './catalog'
import { linkPresentation, recordingLinks } from './labelPresentation'
import { TrackArtists } from './TrackPresentation'
import { LocalTrackList, TrackList } from './TrackList'
import { formatDuration, useUrlTrackView } from './trackView'
import { RecordingEnrichment } from './RecordingEnrichment'

export function TrackListPage() {
  const { isPending } = useAuthenticated()
  const [view, setView] = useUrlTrackView()
  return <Stack spacing={2} sx={{ p: 3, minWidth: 0 }}>
    <Title title="Tracks" /><Typography component="h1" variant="h4">Tracks</Typography>
    <Typography color="text.secondary">One row per Recording (a distinct piece of audio). A Recording released more than once shows its newest release and a release count; open it to see every release. Catalog metadata only, independent of purchases or media files.</Typography>
    {!isPending && <TrackList filter={{}} context="all" view={view} onChange={setView} label="Catalog tracks" groupByRecording />}
  </Stack>
}

function Detail({ id }: { id: string }) {
  const [data, setData] = useState<RecordingDetail>()
  const [error, setError] = useState('')
  const logout = useLogoutIfAccessDenied()
  useEffect(() => {
    const controller = new AbortController()
    void trackClient.recording(id, controller.signal).then(result => { if (!controller.signal.aborted) setData(result) }).catch(failure => {
      if (!controller.signal.aborted) { setError(failure instanceof Error ? failure.message : 'Recording could not be loaded.'); void logout(failure) }
    })
    return () => controller.abort()
  }, [id, logout])
  if (error) return <Alert severity="error">{error}</Alert>
  if (!data) return <LinearProgress aria-label="Loading catalog Recording" />
  return <Stack spacing={3} sx={{ mt: 2, minWidth: 0 }}>
    <Title title={data.title ?? 'Recording'} />
    <Stack spacing={1.5}>
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
    </Stack>
    <Divider />
    <RecordingEnrichment key={id} id={id} />
    <Typography component="h2" variant="h6">Releases of this Recording</Typography>
    <LocalTrackList filter={{ recordingId: id }} context="recording" label="Recording releases" />
    <Accordion><AccordionSummary expandIcon={<ExpandMoreIcon />}><Typography>Details and provider identities</Typography></AccordionSummary>
      <AccordionDetails>
        <Typography sx={{ overflowWrap: 'anywhere' }}>Catalog ID: {data.id}</Typography>
        {data.externalReferences.map(r => <Typography key={`${r.source}:${r.externalId}`}>{r.source}: {r.externalId}{r.isPrimary ? ' (primary)' : ''}</Typography>)}
        <Typography variant="body2" color="text.secondary" sx={{ mt: 2 }}>A Recording is one piece of audio (title, mix and ISRC). Each release it appears on is a separate track listing; media files are not linked yet.</Typography>
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
