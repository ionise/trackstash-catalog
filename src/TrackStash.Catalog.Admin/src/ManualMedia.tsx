import { useEffect, useRef, useState } from 'react'
import { Alert, Button, Checkbox, Dialog, DialogActions, DialogContent, DialogTitle, FormControlLabel, LinearProgress, Stack, TextField, Typography } from '@mui/material'
import { useLogoutIfAccessDenied } from 'react-admin'
import { Link } from 'react-router-dom'
import { mediaClient, trackClient } from './catalog'
import type { CatalogTrack, ManualMediaReview, MediaInventoryItem } from './catalog'
import { formatDuration, trackQuery } from './trackView'

export const manualRecoveryKey = 'trackstash.manualMedia.pending'

export function ManualMedia({ media, recoveryId, onClose, onSaved }: {
  media?: MediaInventoryItem; recoveryId?: string; onClose: () => void; onSaved: () => void
}) {
  const logout = useLogoutIfAccessDenied()
  const [query, setQuery] = useState(media?.title ?? '')
  const [page, setPage] = useState(1)
  const [results, setResults] = useState<{ items: CatalogTrack[]; total: number; pageSize: number }>()
  const [track, setTrack] = useState<CatalogTrack>()
  const [review, setReview] = useState<ManualMediaReview>()
  const [confirmed, setConfirmed] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [recoveryRevision, setRecoveryRevision] = useState(0)
  const active = useRef<AbortController | undefined>(undefined)
  const saved = useRef(onSaved)
  useEffect(() => { saved.current = onSaved }, [onSaved])
  useEffect(() => () => active.current?.abort(), [])
  async function run(task: (signal: AbortSignal) => Promise<void>) {
    if (active.current) return
    const controller = new AbortController()
    active.current = controller; setBusy(true); setError('')
    try { await task(controller.signal) }
    catch (failure) {
      if (!controller.signal.aborted) {
        setError(failure instanceof Error ? failure.message : 'Manual media review failed.')
        void logout(failure)
      }
    } finally {
      if (active.current === controller) { active.current = undefined; setBusy(false) }
    }
  }
  useEffect(() => {
    if (!recoveryId) return
    const controller = new AbortController()
    void mediaClient.manualReview(recoveryId, controller.signal).then(result => {
      if (!controller.signal.aborted) {
        setReview(result)
        if (result.outcome) { sessionStorage.removeItem(manualRecoveryKey); saved.current() }
      }
    }).catch(failure => {
      if (!controller.signal.aborted) {
        setError(`Could not recover review ${recoveryId}: ${failure instanceof Error ? failure.message : 'Outcome unavailable.'}`)
        void logout(failure)
      }
    })
    return () => controller.abort()
  }, [recoveryId, recoveryRevision, logout])
  function search(nextPage: number) {
    void run(async signal => {
      const found = await trackClient.list(trackQuery({}, { page: nextPage, q: query, sort: 'title' }, true), signal)
      setResults(found); setPage(nextPage); setTrack(undefined)
    })
  }
  function saveOutcome(outcome: NonNullable<ManualMediaReview['outcome']>) {
    setReview(current => current ? { ...current, outcome } : current)
    sessionStorage.removeItem(manualRecoveryKey)
    onSaved()
  }
  return <Dialog open onClose={busy || review && !review.outcome || recoveryId && !review ? undefined : onClose} fullWidth maxWidth="md"
    aria-labelledby="manual-media-title">
    <DialogTitle id="manual-media-title">Manually assign media to a Recording</DialogTitle>
    <DialogContent>
      <Stack spacing={2} sx={{ pt: 1 }}>
        <Typography>{(review?.media ?? media)?.title ?? 'Media'} — media {(review?.media ?? media)?.id}</Typography>
        <Alert severity="warning">This is your judgement, not an automatic match. Metadata conflicts remain visible. Verification reads the file and may download a cloud-only copy.</Alert>
        {error && <Alert severity="error">{error}</Alert>}
        {busy && <LinearProgress aria-label="Processing manual media review" />}
        {recoveryId && !review && error && <Stack>
          <Button onClick={() => setRecoveryRevision(value => value + 1)}>Retry loading manual review</Button>
          <Button onClick={() => { sessionStorage.removeItem(manualRecoveryKey); onClose() }}>
            Forget recovery pointer (does not undo a saved assignment)
          </Button>
        </Stack>}
        {!review && !recoveryId && <>
          <TextField label="Search Catalog Recordings" value={query} onChange={event => setQuery(event.target.value)} />
          <Button disabled={busy} onClick={() => search(1)}>Search Recordings</Button>
          {results && <>
            <Typography>{results.total} Recordings found. Page {page}.</Typography>
            {results.items.map(item => <Stack key={item.recordingId} direction="row" spacing={1} sx={{ alignItems: 'center' }}>
              <Button disabled={busy} variant={track?.recordingId === item.recordingId ? 'contained' : 'outlined'}
                onClick={() => setTrack(item)}>Choose {item.title ?? 'Untitled'} {item.mixName ?? ''}</Button>
              <Typography variant="body2">{item.recordingCredits.filter(credit => credit.role === 'primary')
                .map(credit => credit.creditedName ?? credit.artistName).filter(Boolean).join(', ') || 'Unknown Artist'};
                {' '}{item.releaseTitle ?? 'No Release'}; {item.isrc ?? 'No ISRC'}; {formatDuration(item.durationMs)}; {item.recordingId}</Typography>
            </Stack>)}
            <Stack direction="row"><Button disabled={busy || page <= 1} onClick={() => search(page - 1)}>Previous Recordings</Button>
              <Button disabled={busy || page * results.pageSize >= results.total} onClick={() => search(page + 1)}>Next Recordings</Button></Stack>
          </>}
          <Button variant="contained" disabled={busy || !track || !media} onClick={() => {
            if (!track || !media) return
            void run(async signal => {
              const result = await mediaClient.prepareManual(media.id, track.recordingId, signal)
              sessionStorage.setItem(manualRecoveryKey, result.id)
              setReview(result)
            })
          }}>Verify file and review assignment</Button>
        </>}
        {review && <>
          {review.outcome ? <Alert severity="success">Assignment saved to Recording {review.outcome.recordingId}.</Alert> : <>
            <Typography>Assign to: {review.candidate.title} {review.candidate.mix} ({review.candidate.recordingId})</Typography>
            <Link target="_blank" rel="noreferrer" to={`/recordings/${encodeURIComponent(review.candidate.recordingId)}/show`}>Inspect Catalog Recording</Link>
            {review.previousRecordingId && <Alert severity="warning">This replaces the existing link to Recording {review.previousRecordingId}. The previous decision remains in the audit history.</Alert>}
            {review.candidate.evidence.map((item, index) => <Typography variant="body2" key={index}>{item}</Typography>)}
            {review.candidate.blockers.map((item, index) => <Alert severity="warning" key={index}>{item}</Alert>)}
            <FormControlLabel control={<Checkbox checked={confirmed} onChange={event => setConfirmed(event.target.checked)} />}
              label="I reviewed the Recording and any conflicts and confirm this assignment" />
            <Button variant="contained" disabled={busy || !confirmed} onClick={() => void run(async signal => {
              saveOutcome(await mediaClient.applyManual(review, signal))
            })}>Confirm manual assignment</Button>
            <Button disabled={busy} onClick={() => void run(async signal => {
              const current = await mediaClient.manualReview(review.id, signal)
              setReview(current)
              if (current.outcome) saveOutcome(current.outcome)
              else setError('This review has not been applied. Retry the same assignment or check and discard it.')
            })}>Check saved outcome</Button>
          </>}
        </>}
      </Stack>
    </DialogContent>
    <DialogActions>
      {review && !review.outcome && <Button disabled={busy} onClick={() => void run(async signal => {
        const current = await mediaClient.manualReview(review.id, signal)
        if (current.outcome) { setReview(current); saveOutcome(current.outcome); return }
        sessionStorage.removeItem(manualRecoveryKey); onClose()
      })}>Check then discard review</Button>}
      <Button disabled={busy || !!review && !review.outcome || !!recoveryId && !review} onClick={onClose}>Close</Button>
    </DialogActions>
  </Dialog>
}
