import { useState } from 'react'
import { Alert, Box, Button, Checkbox, FormControlLabel, MenuItem, Stack, Table, TableBody, TableCell, TableHead, TableRow, TextField, Typography } from '@mui/material'
import type { EnrichmentBatchReview, EnrichmentBatchSelection } from './catalog'

export function EnrichmentTrackReview({ review, busy, onApply }: {
  review: EnrichmentBatchReview; busy: boolean
  onApply: (selections: EnrichmentBatchSelection[], reason: string, confirmed: boolean) => Promise<void>
}) {
  const [fields, setFields] = useState<Record<string, string[]>>(() => Object.fromEntries(review.tracks.flatMap(track =>
    track.review ? [[track.review.id, track.review.fields.filter(field => field.field !== 'BeatportIdentity' && field.field !== 'PreferredGenre' &&
      (field.current === null || field.current === '' || Array.isArray(field.current) && field.current.length === 0)).map(field => field.field)]] : [])))
  const [preferred, setPreferred] = useState<Record<string, string>>({})
  const [reason, setReason] = useState('')
  const [confirmed, setConfirmed] = useState(false)
  const disabled = busy || !!review.outcome
  const selections: EnrichmentBatchSelection[] = Object.entries(fields).filter(([, values]) => values.length > 0)
    .map(([reviewId, values]) => ({ reviewId, fields: values, preferredGenre: values.includes('PreferredGenre') ? preferred[reviewId] || null : null }))
  const valid = selections.length > 0 && selections.every(s => !s.fields.includes('PreferredGenre') || s.preferredGenre !== null)
  function toggle(id: string, field: string) {
    setFields(current => ({ ...current, [id]: (current[id] ?? []).includes(field)
      ? current[id].filter(value => value !== field) : [...(current[id] ?? []), field] }))
  }
  function display(value: unknown) { return value === null ? 'Not recorded' : typeof value === 'string' ? value : JSON.stringify(value) }
  function classificationChoices(value: unknown): string[] {
    return Array.isArray(value) ? value.filter((item): item is string => typeof item === 'string') : []
  }
  return <Stack component="section" aria-label="Frozen matched track review" spacing={2} sx={{ minWidth: 0 }}>
    <Typography component="h2" variant="h6">Review matched tracks — Beatport Release {review.candidate.candidateId}</Typography>
    <Typography>Frozen {review.createdAt}; batch {review.id} expires after 24 hours. {selections.length} Recordings selected.</Typography>
    {review.warnings.map((warning, index) => <Alert severity="warning" key={index}>{warning}</Alert>)}
    {review.outcome && <Alert severity="success">{review.outcome.recordings.length} Recording updates were saved atomically with provenance and audit history.</Alert>}
    {review.tracks.map((track, index) => <Stack component="section" aria-label={`Track match ${track.candidateId || index}`} spacing={1} key={`${track.candidateId}:${index}`}>
      <Typography component="h3" variant="h6">{track.title} — {track.status}</Typography>
      <Typography>Catalog Recordings: {track.recordingIds.join(', ') || 'None'}. {track.reasons.join(' ')}</Typography>
      {track.review && <>
        <Typography variant="body2">Missing fields are preselected. Existing values and Beatport identity acceptance are not. A Recording update affects all of its Release appearances.</Typography>
        <Box sx={{ minWidth: 0, width: '100%', overflowX: 'auto', contain: 'inline-size' }}>
          <Table size="small" aria-label={`Track fields ${track.candidateId}`}>
            <TableHead><TableRow><TableCell>Select</TableCell><TableCell>Catalog</TableCell><TableCell>Frozen proposal</TableCell><TableCell>Evidence</TableCell></TableRow></TableHead>
            <TableBody>{track.review.fields.map(field => <TableRow key={field.field}>
              <TableCell><FormControlLabel label={field.label} control={<Checkbox disabled={disabled}
                checked={(fields[track.review!.id] ?? []).includes(field.field)} onChange={() => toggle(track.review!.id, field.field)} />} /></TableCell>
              <TableCell>{display(field.current)}</TableCell><TableCell>{display(field.proposed)}</TableCell>
              <TableCell>{track.review!.candidate.evidence.filter(e => e.field === field.field || field.field === 'BeatportIdentity' && e.field === 'ExternalReferences')
                .map((e, i) => <Typography variant="body2" key={i}>{e.source}: {e.sourceRecordId}; {e.retrievedAt}</Typography>)}</TableCell>
            </TableRow>)}</TableBody>
          </Table>
        </Box>
        {track.review.fields.some(f => f.field === 'PreferredGenre') && <TextField select label={`Preferred classification for ${track.candidateId}`}
          disabled={disabled || !(fields[track.review.id] ?? []).includes('PreferredGenre')} value={preferred[track.review.id] ?? ''}
          onChange={event => setPreferred(current => ({ ...current, [track.review!.id]: event.target.value }))}>
          <MenuItem value="">Choose classification</MenuItem>
          {classificationChoices(track.review.fields.find(f => f.field === 'PreferredGenre')?.proposed).map(value => <MenuItem key={value} value={value}>{value}</MenuItem>)}
        </TextField>}
      </>}
    </Stack>)}
    <TextField label="Track batch review reason" value={reason} disabled={disabled} multiline
      slotProps={{ htmlInput: { maxLength: 2000 } }} onChange={event => setReason(event.target.value)} />
    <FormControlLabel label="I confirm this provider Release/version and these matched Recording updates"
      control={<Checkbox checked={confirmed} disabled={disabled} onChange={event => setConfirmed(event.target.checked)} />} />
    <Button variant="contained" disabled={disabled || !valid || !reason.trim() || !confirmed}
      onClick={() => void onApply(selections, reason, confirmed)}>Apply selected track metadata</Button>
  </Stack>
}
