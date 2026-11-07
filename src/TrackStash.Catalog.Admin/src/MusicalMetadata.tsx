import { Stack, Typography } from '@mui/material'
import type { CatalogMusicalMetadata } from './catalog'

export function MusicalMetadata({ metadata }: { metadata?: CatalogMusicalMetadata | null }) {
  if (!metadata) return null
  return <Stack spacing={0.5}>
    {metadata.bpm !== null && <Typography>BPM: {metadata.bpm}</Typography>}
    {metadata.keyValue && <Typography>Musical key: {metadata.keyValue} ({metadata.keyNotation})</Typography>}
    {!!metadata.genres?.length && <Typography>Genres: {metadata.genres.join(', ')}</Typography>}
    {!!metadata.subgenres?.length && <Typography>Subgenres: {metadata.subgenres.join(', ')}</Typography>}
    {metadata.preferredGenre && <Typography>Preferred classification: {metadata.preferredGenre}</Typography>}
  </Stack>
}
