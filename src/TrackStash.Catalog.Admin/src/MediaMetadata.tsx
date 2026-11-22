import { Box, Typography } from '@mui/material'
import { Fragment } from 'react'
import type { MediaFileTags } from './catalog'

export function MediaMetadata({ item }: { item: {
  title: string | null; mix?: string | null; artist: string | null; album: string | null
  label?: string | null; release?: string | null; isrc?: string | null; durationSeconds?: number | null
  format?: string | null; sizeBytes?: number; path: string; scannedUtc: string; lastModifiedUtc?: string | null
  tags?: MediaFileTags | null
} }) {
  const fields: [string, string | number | null | undefined][] = [
    ['Title', item.title], ['Mix / subtitle', item.mix], ['Artist', item.artist],
    ['Artists (scanner)', item.tags?.artists], ['Remixers (scanner)', item.tags?.remixers],
    ['Album', item.album], ['Release', item.release], ['Label', item.label],
    ['ISRC', item.isrc], ['Barcode', item.tags?.barcode], ['Catalog number', item.tags?.catalogNumber],
    ['Track number', item.tags?.trackNumber], ['Disc number', item.tags?.discNumber],
    ['BPM', item.tags?.bpm], ['Key', item.tags?.musicalKey], ['Genre', item.tags?.genre], ['Year', item.tags?.year],
    ['Duration (seconds)', item.durationSeconds], ['Format', item.format], ['Size (bytes)', item.sizeBytes],
    ['Path', item.path], ['Last modified', item.lastModifiedUtc], ['Last scanned', item.scannedUtc],
  ]
  return <Box component="dl" sx={{ overflowWrap: 'anywhere' }}>
    {fields.map(([label, value]) => <Fragment key={label}>
      <Typography component="dt" variant="body2" sx={{ fontWeight: 'bold' }}>{label}</Typography>
      <Typography component="dd" variant="body2" sx={{ m: 0, mb: 1 }}>{value === null || value === undefined || value === '' ? 'Not recorded' : String(value)}</Typography>
    </Fragment>)}
  </Box>
}
