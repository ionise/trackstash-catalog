import { useEffect, useRef, useState } from 'react'
import { Accordion, AccordionDetails, AccordionSummary, Alert, Button, Checkbox, Chip, FormControlLabel, LinearProgress, MenuItem, Stack, Step, StepLabel, Stepper, TextField, Typography } from '@mui/material'
import { useLogoutIfAccessDenied } from 'react-admin'
import { Link } from 'react-router-dom'
import { mediaClient, mediaLibraryName } from './catalog'
import type { MediaGroup, MediaReconciliationFile, MediaReconciliationOutcome, MediaReconciliationPlan, MediaReconciliationReview } from './catalog'

export function MediaReconciliation({ group, libraryNames, onSaved }: {
  group: MediaGroup; libraryNames: Record<string, string>; onSaved: (outcome: MediaReconciliationOutcome) => void
}) {
  const logout = useLogoutIfAccessDenied()
  const [search, setSearch] = useState(group.release ?? group.name)
  const [results, setResults] = useState<Awaited<ReturnType<typeof mediaClient.releaseSearch>>>()
  const [plan, setPlan] = useState<MediaReconciliationPlan>()
  const [choices, setChoices] = useState<Record<number, string>>({})
  const [review, setReview] = useState<MediaReconciliationReview>()
  const [reason, setReason] = useState('')
  const [confirmed, setConfirmed] = useState(false)
  const [error, setError] = useState('')
  const [busy, setBusy] = useState('')
  const pending = useRef<AbortController | undefined>(undefined)
  const saved = !!review?.outcome
  useEffect(() => () => pending.current?.abort(), [])
  async function action(label: string, task: (signal: AbortSignal) => Promise<void>) {
    if (pending.current) return
    const controller = new AbortController()
    pending.current = controller; setBusy(label); setError('')
    try { await task(controller.signal) } catch (failure) {
      if (!controller.signal.aborted) {
        setError(failure instanceof Error ? failure.message : 'Release reconciliation failed.'); void logout(failure)
      }
    } finally { if (!controller.signal.aborted) { pending.current = undefined; setBusy('') } }
  }
  function resetReview() { setReview(undefined); setReason(''); setConfirmed(false) }
  function loadMatches(id: string) {
    void action('Loading Release track proposals (no audio reads)', async signal => {
      const result = await mediaClient.reconcile(group.key, id, signal)
      if (!signal.aborted) { setPlan(result); setChoices({}); resetReview() }
    })
  }
  const selections = Object.entries(choices).map(([id, recordingId]) => ({ mediaId: Number(id), recordingId }))
  const ready = plan?.files.filter(f => f.suggestedRecordingId && !f.linkedRecordingId && !f.alreadyRegistered) ?? []
  const attention = plan?.files.filter(f => !f.suggestedRecordingId && !f.linkedRecordingId) ?? []
  const linked = plan?.files.filter(f => f.linkedRecordingId) ?? []
  const proposedTrack = (file: MediaReconciliationFile) => choices[file.media.id] ?? file.suggestedRecordingId ?? file.linkedRecordingId
  const missingTracks = plan?.tracks.filter(t => !plan.files.some(f => proposedTrack(f) === t.recordingId)) ?? []
  function select(id: number, recordingId: string | undefined) {
    const updated = { ...choices }
    if (recordingId) updated[id] = recordingId
    else delete updated[id]
    setChoices(updated); resetReview()
  }
  function copy(file: MediaReconciliationFile) {
    const recordingId = choices[file.media.id] ?? file.suggestedRecordingId ?? ''
    const candidate = file.candidates.find(c => c.recordingId === recordingId)
    const locked = !!busy || !!review || !!file.linkedRecordingId || file.alreadyRegistered
    return <Stack key={file.media.id} spacing={1} sx={{ p: 1, borderBottom: 1, borderColor: 'divider' }}>
      <Typography>{file.media.title ?? 'Untitled'} {file.media.mix} — {file.media.artist ?? 'Unknown Artist'}</Typography>
      <Stack direction="row" spacing={1} sx={{ flexWrap: 'wrap', gap: 1 }}>
        <Chip size="small" label={mediaLibraryName(file.media.library, libraryNames)} />
        <Chip size="small" label={`Media ${file.media.id}`} />
        {file.media.fingerprintKey && <Chip size="small" label="Stored audio fingerprint" />}
      </Stack>
      {file.linkedRecordingId ? <Typography>Already linked to <Link to={`/recordings/${encodeURIComponent(file.linkedRecordingId)}/show`}>{file.linkedRecordingId}</Link>; not changed.</Typography>
        : file.alreadyRegistered ? <Alert severity="warning">Already registered. Re-association requires separate review.</Alert> : <>
          <TextField select label={`Catalog track for media ${file.media.id}`} value={recordingId} disabled={locked}
            onChange={event => select(file.media.id, event.target.value || undefined)}>
            <MenuItem value="">Leave unassigned</MenuItem>
            {file.candidates.map(c => <MenuItem key={c.recordingId} value={c.recordingId} disabled={!!c.blockers.length}>
              {c.title} — {c.mix ?? 'Version unknown'} ({c.status})
            </MenuItem>)}
          </TextField>
          <FormControlLabel label={`Include media ${file.media.id}`} control={<Checkbox checked={!!choices[file.media.id]}
            disabled={locked || !candidate || !!candidate.blockers.length}
            onChange={event => select(file.media.id, event.target.checked ? recordingId : undefined)} />} />
        </>}
      <Accordion disableGutters>
        <AccordionSummary>Match evidence and exceptions for media {file.media.id}</AccordionSummary>
        <AccordionDetails>
          {(candidate ? [candidate] : file.candidates).map(c => <Stack key={c.recordingId} spacing={1}>
            <Typography>{c.title} / {c.mix}: {c.status}</Typography>
            {c.evidence.map((e, i) => <Typography variant="body2" key={`e${i}`}>{e}</Typography>)}
            {c.blockers.map((b, i) => <Alert severity="warning" key={`b${i}`}>{b}</Alert>)}
          </Stack>)}
          {!file.candidates.length && <Typography>No existing tracks on the selected Release.</Typography>}
        </AccordionDetails>
      </Accordion>
    </Stack>
  }
  return <Stack component="section" aria-label="Guided Release reconciliation" spacing={2} sx={{ minWidth: 0, overflowWrap: 'anywhere' }}>
    <Typography variant="h5" component="h2">Organise this Release</Typography>
    <Stepper activeStep={saved ? 3 : review ? 2 : plan ? 1 : 0} orientation="vertical">
      {['Choose Catalog Release', 'Review tracks and copies', 'Verify and confirm together'].map(label =>
        <Step key={label}><StepLabel>{label}</StepLabel></Step>)}
    </Stepper>
    <Alert severity="info">Work on the entire Release group, including files outside the current filters or page. Select supported matches together; leave uncertain versions for later. Nothing is assigned until the final confirmation.</Alert>
    {error && <Alert severity="error">{error}</Alert>}
    {busy && <>
      <Typography role="status">{busy}. File verification may download cloud-only media and take some time.</Typography>
      <LinearProgress aria-label={busy} />
      <Button onClick={() => {
        pending.current?.abort(); pending.current = undefined; setBusy('')
        setError(review ? 'Request cancelled. A save may have completed; check the saved outcome or retry this same review.'
          : 'Verification/request cancelled. No Recording links were accepted; you can retry.')
      }}>Cancel reconciliation request</Button>
    </>}
    {!plan && <>
      {group.associatedReleaseId && <Alert severity="info">This group is already associated with a Release. Continue by matching its tracks.</Alert>}
      {group.associatedReleaseId ? <Button disabled={!!busy} onClick={() => loadMatches(group.associatedReleaseId!)}>Load associated Release tracks</Button> : <>
        <TextField label="Search Catalog Releases for reconciliation" value={search} disabled={!!busy}
          slotProps={{ htmlInput: { maxLength: 200 } }} onChange={event => { setSearch(event.target.value); setResults(undefined) }} />
        <Button disabled={!!busy} onClick={() => void action('Finding Catalog Releases', async signal => {
          const result = await mediaClient.releaseSearch(search, 1, signal)
          if (!signal.aborted) setResults(result)
        })}>Find Release for track matching</Button>
        {results && <>
          {!results.items.length && <Alert severity="warning">No Catalog Releases found. Change the search or admit the Release separately; this workflow does not create tracks.</Alert>}
          {results.items.map(release => <Stack key={release.id} spacing={1}>
            <Typography>{release.title ?? release.id} / {release.labelName ?? 'Unknown Label'}</Typography>
            <Button disabled={!!busy} onClick={() => loadMatches(release.id)}>Choose Release {release.title ?? release.id}</Button>
          </Stack>)}
          <Stack direction="row"><Button disabled={!!busy || results.page <= 1} onClick={() => void action('Finding Catalog Releases', async signal => {
            const result = await mediaClient.releaseSearch(search, results.page - 1, signal); if (!signal.aborted) setResults(result)
          })}>Previous reconciliation Releases</Button>
            <Button disabled={!!busy || results.page * results.pageSize >= results.total} onClick={() => void action('Finding Catalog Releases', async signal => {
              const result = await mediaClient.releaseSearch(search, results.page + 1, signal); if (!signal.aborted) setResults(result)
            })}>Next reconciliation Releases</Button></Stack>
        </>}
      </>}
    </>}
    {plan && <>
      <Typography variant="h6" component="h3">{group.name} → {plan.catalogTitle}</Typography>
      {plan.evidence.map((e, i) => <Typography variant="body2" key={i}>{e}</Typography>)}
      <Typography>{plan.tracks.length} Catalog Recordings · {plan.files.length} media files · {ready.length} supported proposals · {attention.length} need attention · {linked.length} already linked.</Typography>
      {!review && <>
        <Stack direction={{ xs: 'column', sm: 'row' }} spacing={1}>
          <Button disabled={!!busy || !ready.length} onClick={() => setChoices({ ...Object.fromEntries(ready.map(f => [f.media.id, f.suggestedRecordingId!])), ...choices })}>
            Select confident matches
          </Button>
          <Button disabled={!!busy} onClick={() => setChoices({})}>Clear selection</Button>
          <Button disabled={!!busy} onClick={() => loadMatches(plan.releaseId)}>Reload track matches</Button>
          {!plan.releaseAlreadyAssociated && <Button disabled={!!busy} onClick={() => { setPlan(undefined); setChoices({}); resetReview() }}>Choose a different Release</Button>}
        </Stack>
        {plan.tracks.map(track => {
          const files = plan.files.filter(f => proposedTrack(f) === track.recordingId)
          if (!files.length) return null
          const selectable = files.filter(f => !f.linkedRecordingId && !f.alreadyRegistered && f.candidates.some(c => c.recordingId === track.recordingId && !c.blockers.length))
          return <Accordion key={track.recordingId} disableGutters>
            <AccordionSummary>{track.title} — {track.mix ?? 'Version unknown'} · {files.length} copies · {files.filter(f => choices[f.media.id]).length} selected · {Array.from(new Set(files.map(f => mediaLibraryName(f.media.library, libraryNames)))).join(', ')}</AccordionSummary>
            <AccordionDetails>
              <Typography>{track.artists.join(' & ') || 'Unknown Artist'}</Typography>
              {!!selectable.length && <Button disabled={!!busy} onClick={() => setChoices({ ...choices,
                ...Object.fromEntries(selectable.map(f => [f.media.id, track.recordingId])) })}>Include eligible copies of {track.title}</Button>}
              {!files.length && <Typography>No confidently proposed media copies for this track.</Typography>}
              {files.map(copy)}
            </AccordionDetails>
          </Accordion>
        })}
        {!!missingTracks.length && <Accordion disableGutters>
          <AccordionSummary>Catalog tracks without proposed copies — {missingTracks.length}</AccordionSummary>
          <AccordionDetails>{missingTracks.map(t => <Typography key={t.recordingId}>{t.title} — {t.mix}. No media assigned by this proposal.</Typography>)}</AccordionDetails>
        </Accordion>}
        {!!attention.length && <Accordion disableGutters>
          <AccordionSummary>Needs attention — {attention.length} files (optional; can be left for later)</AccordionSummary>
          <AccordionDetails>{attention.map(copy)}</AccordionDetails>
        </Accordion>}
        <Typography>{selections.length} selected copies across {new Set(selections.map(s => s.recordingId)).size} Recordings. Unselected files will not be linked.</Typography>
        <Alert severity="warning">Verification reads the selected files and any copies supporting their fingerprint matches. Missing scanner SHA-256 hashes are calculated here and frozen in the review; no rescan is needed just for a missing hash. Each copy has its own byte hash, which may differ with tags. Existing scanner-hash mismatches still require refreshing that file. Verification may download Dropbox media. Exact audio matches do not prove a Release or byte-identical backups.</Alert>
        <Button variant="contained" disabled={!!busy || !selections.length} onClick={() => void action(`Verifying ${selections.length} selected media files and supporting copies`, async signal => {
          const result = await mediaClient.prepareReconciliation(plan, selections, signal)
          if (!signal.aborted) { setReview(result); setConfirmed(false); setReason('') }
        })}>Verify selected files and review</Button>
      </>}
    </>}
    {review && <Stack component="section" aria-label="Verified Release reconciliation" spacing={1}>
      {saved ? <>
        <Alert severity="success">{review.outcome!.links.length} media copies linked and Release association saved with permanent audit.</Alert>
        <Link to={`/releases/${encodeURIComponent(review.plan.releaseId)}/show`}>Open reconciled Release</Link>
        <Typography>Review {review.id}. Saved {review.outcome!.appliedAt}. Unselected files remain for later.</Typography>
        <Button onClick={() => { resetReview(); loadMatches(review.plan.releaseId) }}>Continue with remaining files</Button>
      </> : <>
        <Typography variant="h6" component="h3">Final check: {review.plan.catalogTitle}</Typography>
        <Typography>{review.selections.length} selected files · {review.verifiedMediaIds.length} files verified including supporting copies. Review expires 24 hours after {review.createdAt}.</Typography>
        <Typography variant="body2">Review {review.id}. Check saved outcome after a cancelled or uncertain save; retrying this same review cannot duplicate its links.</Typography>
        {review.selections.map(selection => {
          const file = review.plan.files.find(f => f.media.id === selection.mediaId)!
          const track = review.plan.tracks.find(t => t.recordingId === selection.recordingId)!
          const candidate = file.candidates.find(c => c.recordingId === selection.recordingId)!
          return <Stack key={selection.mediaId} spacing={1}>
            <Typography>{file.media.title} / {mediaLibraryName(file.media.library, libraryNames)} → {track.title} — {track.mix}</Typography>
            <Typography variant="body2">{candidate.status}. {candidate.evidence.join(' ')}</Typography>
          </Stack>
        })}
        <Alert severity="info">One confirmation saves the whole-group Release association and all selected file links atomically. If any verification fails, none of this batch's links are saved.</Alert>
        <TextField label="Reconciliation reason" multiline value={reason} disabled={!!busy}
          slotProps={{ htmlInput: { maxLength: 2000 } }} onChange={event => setReason(event.target.value)} />
        <FormControlLabel label="I confirm the entire group's Release and all selected Recording versions"
          control={<Checkbox checked={confirmed} disabled={!!busy} onChange={event => setConfirmed(event.target.checked)} />} />
        <Button variant="contained" disabled={!!busy || !confirmed || !reason.trim()} onClick={() => void action('Rechecking and saving selected Release and Recording links', async signal => {
          const outcome = await mediaClient.applyReconciliation(review, reason, confirmed, signal)
          if (!signal.aborted) { setReview({ ...review, outcome }); onSaved(outcome) }
        })}>Confirm Release and selected tracks</Button>
        <Button disabled={!!busy} onClick={() => void action('Checking saved reconciliation outcome', async signal => {
          const result = await mediaClient.reconciliationReview(review.id, signal)
          if (!signal.aborted) { setReview(result); if (result.outcome) onSaved(result.outcome) }
        })}>Check saved outcome</Button>
        <Button disabled={!!busy} onClick={resetReview}>Back to track selection</Button>
      </>}
    </Stack>}
  </Stack>
}
