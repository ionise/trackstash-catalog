import { Stack, Tooltip, Typography } from '@mui/material'
import { Link } from 'react-router-dom'
import type { ReleaseCredit } from './catalog'

export function TrackTitleArtists({ title, mixName, credits }: {
  title: string | null; mixName: string | null; credits: ReleaseCredit[]
}) {
  const primary = credits.filter(c => c.role === 'primary')
  const remixers = credits.filter(c => c.role === 'remixer')
  function names(items: ReleaseCredit[]) {
    return items.map((credit, index) => <span key={`${credit.artistId}:${index}`}>
      {index > 0 && ', '}
      <Tooltip describeChild title={`View Artist: ${credit.artistName ?? credit.creditedName ?? credit.artistId}${credit.role === 'remixer' ? ' (remixer)' : ''}`}>
        <Link to={`/artists/${encodeURIComponent(credit.artistId)}/show`}>{credit.creditedName ?? credit.artistName ?? credit.artistId}</Link>
      </Tooltip>
      {credit.identityStatus === 'unresolved_compound' && ' [unresolved compound]'}
    </span>)
  }
  return <Stack spacing={0.5}>
    <Typography sx={{ fontWeight: 600, overflowWrap: 'anywhere' }}>{title ?? 'Untitled recording'}{mixName && <Typography component="span" color="text.secondary"> {mixName}</Typography>}</Typography>
    <Typography variant="body2">{primary.length ? names(primary) : 'Primary Artist not recorded'}
      {remixers.length > 0 && <> · {names(remixers)}</>}
    </Typography>
  </Stack>
}
