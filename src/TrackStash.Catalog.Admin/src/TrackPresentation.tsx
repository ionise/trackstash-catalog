import { Box, Stack, Tooltip, Typography } from '@mui/material'
import AlbumIcon from '@mui/icons-material/Album'
import MusicNoteIcon from '@mui/icons-material/MusicNote'
import QueueMusicIcon from '@mui/icons-material/QueueMusic'
import { Link } from 'react-router-dom'
import type { ReleaseCredit } from './catalog'

function names(items: ReleaseCredit[]) {
  return items.map((credit, index) => <span key={`${credit.artistId}:${index}`}>
    {index > 0 && ', '}
    <Tooltip describeChild title={`View Artist: ${credit.artistName ?? credit.creditedName ?? credit.artistId}${credit.role === 'remixer' ? ' (remixer)' : ''}`}>
      <Link to={`/artists/${encodeURIComponent(credit.artistId)}/show`}>{credit.creditedName ?? credit.artistName ?? credit.artistId}</Link>
    </Tooltip>
    {credit.identityStatus === 'unresolved_compound' && ' [unresolved compound]'}
  </span>)
}

export function TrackArtists({ credits }: { credits: ReleaseCredit[] }) {
  const primary = credits.filter(c => c.role === 'primary')
  const remixers = credits.filter(c => c.role === 'remixer')
  return <Typography variant="body2">{primary.length ? names(primary) : 'Primary Artist not recorded'}
    {remixers.length > 0 && <> · {names(remixers)}</>}
  </Typography>
}

export function TrackTitleArtists({ title, mixName, credits, to }: {
  title: string | null; mixName: string | null; credits: ReleaseCredit[]; to?: string
}) {
  return <Stack spacing={0.5}>
    <Typography sx={{ fontWeight: 600, overflowWrap: 'anywhere' }}>{to ? <Link to={to}>{title ?? 'Untitled recording'}</Link> : title ?? 'Untitled recording'}{mixName && <Typography component="span" color="text.secondary"> {mixName}</Typography>}</Typography>
    <TrackArtists credits={credits} />
  </Stack>
}

export function CreditEvidence({ credits }: { credits: { role: string; scope: string; creditedName: string | null }[] }) {
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
