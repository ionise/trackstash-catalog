import { useCallback, useEffect, useRef, useState } from 'react'
import { Accordion, AccordionDetails, AccordionSummary, Alert, Box, Button, Checkbox, Chip, LinearProgress, MenuItem, Stack, Table, TableBody, TableCell, TableHead, TableRow, TextField, Typography } from '@mui/material'
import { useLogoutIfAccessDenied } from 'react-admin'
import { Link } from 'react-router-dom'
import { mediaClient, mediaLibraryName } from './catalog'
import type { MediaInventoryItem, MediaMatchPlan, MediaMatchReview, MediaMatchRow, MediaMatchSelection } from './catalog'
import { ManualMedia, manualRecoveryKey } from './ManualMedia'

const recoveryKey = 'trackstash.mediaMatches.pending'
function outcomeMessage(review: MediaMatchReview) {
  return review.outcome ? `${review.outcome.links.length} copies linked; ${review.outcome.classifications.length} classifications saved. Permanent judgement audit recorded. No Releases were assigned or created.` : ''
}

export function MediaMatches({ items, libraryNames, onBusy, onSaved, onFindRelease }: {
  items: MediaInventoryItem[]; libraryNames: Record<string, string>; onBusy: (busy: boolean) => void; onSaved: () => void
  onFindRelease?: (items: MediaInventoryItem[]) => void
}) {
  const logout = useLogoutIfAccessDenied()
  const [loaded, setLoaded] = useState<{ items: MediaInventoryItem[]; revision: number; plan?: MediaMatchPlan; error?: string }>()
  const [selected, setSelected] = useState<Record<number, string>>({})
  const [classification, setClassification] = useState('')
  const [view, setView] = useState('all')
  const [review, setReview] = useState<MediaMatchReview>()
  const [error, setError] = useState('')
  const [success, setSuccess] = useState('')
  const [busy, setBusy] = useState(false)
  const [revision, setRevision] = useState(0)
  const [recoveryId, setRecoveryId] = useState(() => sessionStorage.getItem(recoveryKey) ?? '')
  const [recoveryRevision, setRecoveryRevision] = useState(0)
  const [manual, setManual] = useState<{ media?: MediaInventoryItem; recoveryId?: string } | undefined>(() => {
    const id = sessionStorage.getItem(manualRecoveryKey)
    return id ? { recoveryId: id } : undefined
  })
  const pending = useRef<AbortController | undefined>(undefined)
  const pendingReview = useRef<MediaMatchReview | undefined>(undefined)
  const current = loaded?.items === items && loaded.revision === revision ? loaded : undefined
  const plan = current?.plan
  const loading = items.length > 0 && !current
  useEffect(() => () => { pending.current?.abort(); onBusy(false) }, [onBusy])
  useEffect(() => { if (manual) onBusy(true) }, [manual, onBusy])
  const manualSaved = useCallback(() => { setRevision(value => value + 1); onSaved() }, [onSaved])
  useEffect(() => {
    if (!recoveryId) return
    const controller = new AbortController()
    void mediaClient.matchReview(recoveryId, controller.signal).then(result => {
      if (controller.signal.aborted) return
      setReview(result); setRecoveryId(''); setError('')
      if (result.outcome) {
        sessionStorage.removeItem(recoveryKey); setSuccess(outcomeMessage(result)); onBusy(false)
      } else {
        pendingReview.current = result; onBusy(true)
      }
    }).catch(failure => {
      if (!controller.signal.aborted) {
        setError(`Could not recover batch ${recoveryId}: ${failure instanceof Error ? failure.message : 'Outcome unavailable.'}`)
        void logout(failure)
      }
    })
    return () => controller.abort()
  }, [recoveryId, recoveryRevision, logout, onBusy])
  useEffect(() => {
    const controller = new AbortController()
    if (items.length) void mediaClient.matchProposals(items.map(f => f.id), controller.signal).then(result => {
      if (!controller.signal.aborted) { setLoaded({ items, revision, plan: result }); setSelected({}) }
    }).catch(failure => {
      if (!controller.signal.aborted) {
        setLoaded({ items, revision, error: failure instanceof Error ? failure.message : 'Media proposals failed.' }); void logout(failure)
      }
    })
    return () => controller.abort()
  }, [items, logout, revision])
  async function action(task: (signal: AbortSignal) => Promise<void>) {
    if (pending.current) return
    const controller = new AbortController()
    pending.current = controller; setBusy(true); onBusy(true); setError(''); setSuccess('')
    try { await task(controller.signal) }
    catch (failure) {
      if (!controller.signal.aborted) {
        setError(failure instanceof Error ? failure.message : 'Media acceptance failed.'); void logout(failure)
      }
    } finally {
      if (pending.current === controller) { pending.current = undefined; setBusy(false); onBusy(!!pendingReview.current) }
    }
  }
  function saved(result: MediaMatchReview) {
    if (!result.outcome) return
    pendingReview.current = undefined
    sessionStorage.removeItem(recoveryKey)
    setReview(result); setSelected({})
    setSuccess(outcomeMessage(result))
    onSaved()
  }
  const visible = plan?.files.filter(row => view === 'all' || view === 'linked' && row.alreadyRegistered ||
    view === 'supported' && row.suggestedRecordingId || view === 'attention' && !row.alreadyRegistered && !row.suggestedRecordingId) ?? []
  function eligible(row: MediaMatchRow) {
    return row.classification !== classification && !!classification ||
      !row.alreadyRegistered && row.candidates.some(c => c.blockers.length === 0)
  }
  const selections: MediaMatchSelection[] = plan?.files.filter(f => Object.hasOwn(selected, f.media.id)).map(f => ({
    mediaId: f.media.id, recordingId: selected[f.media.id] || null, classification: classification || null,
  })) ?? []
  const valid = selections.length > 0 && selections.every(s => s.recordingId || s.classification)
  const frozen = !!review && !review.outcome
  const disabled = busy || frozen || !!recoveryId
  return <Stack component="section" aria-label="Bulk media matching" spacing={2} sx={{ minWidth: 0 }}>
    <Alert severity="info">
      Tick the proposed mappings you agree with, then Accept selected. Your selection is the judgement: no typed reason or second confirmation.
      Acceptance verifies each selected file and any exact-fingerprint supporting copies, then saves the entire batch or none.
      This links existing Recordings only; Release identity is separate. Unmatched edits, bootlegs and remixes require separate review.
    </Alert>
    <Typography variant="body2">Browsing proposals does not read audio. Acceptance reads bytes and may download cloud-only files. SHA-256 freezes each copy individually; hashes need not match across formats or tags.</Typography>
    {error && <Alert severity="error">{error}</Alert>}
    {current?.error && <Alert severity="error">{current.error}</Alert>}
    {success && <Alert severity="success">{success}</Alert>}
    {recoveryId && <Stack spacing={1}>
      <Alert severity="warning">Recovering saved batch {recoveryId} before allowing another acceptance. This pointer survives page reloads in this tab.</Alert>
      <Button disabled={busy} onClick={() => setRecoveryRevision(value => value + 1)}>Retry loading saved batch</Button>
      <Button disabled={busy} onClick={() => {
        sessionStorage.removeItem(recoveryKey); setRecoveryId(''); setError('')
      }}>Forget recovery pointer (does not undo saved links)</Button>
    </Stack>}
    {(loading || busy) && <LinearProgress aria-label={busy ? 'Verifying and accepting selected media' : 'Loading media proposals'} />}
    {busy && <Button onClick={() => {
      pending.current?.abort(); pending.current = undefined; setBusy(false); onBusy(!!pendingReview.current)
      setError(pendingReview.current ? 'Acceptance interrupted; it may already have saved. Check the saved outcome or retry the same batch.'
        : 'Verification cancelled before acceptance. No links or classifications were requested.')
    }}>Cancel matching request</Button>}
    <Stack direction={{ xs: 'column', sm: 'row' }} spacing={1}>
      <TextField select label="Show on this page" value={view} disabled={disabled} onChange={event => {
        setView(event.target.value); setSelected({})
      }}>
        <MenuItem value="all">All files</MenuItem><MenuItem value="supported">Unique supported proposals</MenuItem>
        <MenuItem value="attention">Needs attention / unmatched</MenuItem><MenuItem value="linked">Already registered</MenuItem>
      </TextField>
      <TextField select label="Classify selected media" value={classification} disabled={disabled} onChange={event => {
        setClassification(event.target.value); setSelected({})
      }}>
        <MenuItem value="">Do not change classification</MenuItem>
        <MenuItem value="unofficial">Unofficial / self-released</MenuItem><MenuItem value="unclassified">Clear classification</MenuItem>
      </TextField>
    </Stack>
    <Typography variant="body2">Classification is independent of Label and applies only to checked files, including unmatched files. It does not declare any Recording or Release unofficial and never creates a pseudolabel.</Typography>
    <Stack direction="row" spacing={1} sx={{ flexWrap: 'wrap' }} useFlexGap>
      <Button disabled={disabled || !plan} onClick={() => setSelected(Object.fromEntries(visible
        .filter(row => row.suggestedRecordingId).map(row => [row.media.id, row.suggestedRecordingId!])))}>
        Select supported proposals on this page
      </Button>
      <Button disabled={disabled || !selections.length} onClick={() => setSelected({})}>Clear selection</Button>
      <Button disabled={disabled || loading} onClick={() => setRevision(value => value + 1)}>Reload proposals</Button>
    </Stack>
    <Typography>{selections.length} selected files on this page. Changing page, filters or grouping clears selection; no hidden pages are selected.</Typography>
    <Box sx={{ width: '100%', overflowX: 'auto', contain: 'inline-size' }}>
      <Table size="small" aria-label="Media match proposals">
        <TableHead><TableRow><TableCell>Select</TableCell><TableCell>Media / library</TableCell><TableCell>Proposed Catalog Recording</TableCell><TableCell>Evidence / details</TableCell></TableRow></TableHead>
        <TableBody>{visible.map(row => {
          const checked = Object.hasOwn(selected, row.media.id)
          const recordingId = checked ? selected[row.media.id] : row.suggestedRecordingId ?? ''
          const candidate = row.candidates.find(c => c.recordingId === recordingId)
          return <TableRow key={row.media.id}>
            <TableCell><Checkbox slotProps={{ input: { 'aria-label': `Select media ${row.media.id}` } }} checked={checked}
              disabled={disabled || !eligible(row)} onChange={event => setSelected(current => {
                const next = { ...current }
                if (event.target.checked) next[row.media.id] = recordingId
                else delete next[row.media.id]
                return next
              })} /></TableCell>
            <TableCell><Typography>{row.media.title ?? 'Untitled'} {row.media.mix ?? ''}</Typography>
              <Typography variant="body2">{row.media.artist ?? 'Unknown Artist'}</Typography>
              <Chip size="small" label={mediaLibraryName(row.media.library, libraryNames)} />
              {row.classification === 'unofficial' && <Chip size="small" label="Unofficial / self-released" />}
              <Typography variant="body2">{row.media.label ?? 'Unknown Label'} / {row.media.release ?? row.media.album ?? 'Unknown Release'}</Typography>
            </TableCell>
            <TableCell>{row.alreadyRegistered ? <Typography>{row.linkedRecordingId
              ? <Link to={`/recordings/${encodeURIComponent(row.linkedRecordingId)}/show`}>Already linked</Link> : 'Already registered; reassignment is a separate review.'}</Typography>
              : <TextField select fullWidth label={`Recording for media ${row.media.id}`} value={recordingId}
                disabled={disabled} onChange={event => setSelected(current => ({ ...current, [row.media.id]: event.target.value }))}>
                <MenuItem value="">No Recording selected</MenuItem>
                {row.candidates.map(c => <MenuItem key={c.recordingId} value={c.recordingId} disabled={c.blockers.length > 0}>
                  {c.title} {c.mix} — {c.status} ({c.recordingId})
                </MenuItem>)}
              </TextField>}
              {!row.alreadyRegistered && !row.candidates.length && <Typography>No existing Catalog match found using tagged ISRC, title or exact-fingerprint copies.</Typography>}
              {!row.alreadyRegistered && row.candidates.filter(c => c.blockers.length > 0).map(c =>
                <Alert severity="warning" key={c.recordingId}>
                  Cannot select {c.title} {c.mix} ({c.recordingId}): {c.blockers.join(' ')}
                </Alert>)}
              {!row.alreadyRegistered && onFindRelease && <Button disabled={disabled} onClick={() => onFindRelease(
                plan!.files.filter(f => !f.alreadyRegistered && (f.media.id === row.media.id ||
                  row.media.fingerprintKey && f.media.fingerprintKey === row.media.fingerprintKey)).map(f => f.media)
              )}>Find Release and admit for media {row.media.id}</Button>}
              <Button disabled={disabled || row.alreadyRegistered && !row.linkedRecordingId} onClick={() => setManual({ media: row.media })}>
                {row.linkedRecordingId ? 'Reassign' : 'Manually assign'} media {row.media.id}
              </Button>
              {!recordingId && !row.alreadyRegistered && row.candidates.some(c => c.status === 'Needs manual review: matches linked audio') &&
                <Alert severity="warning">Matches audio already linked to a Recording. Tags differ or are incomplete; choose that Recording above to accept your judgement.</Alert>}
              {candidate && <><Typography variant="body2">{candidate.status}</Typography>
                {candidate.evidence.filter(e => e.startsWith('Metadata warning:')).map((warning, index) =>
                  <Alert severity="warning" key={index}>{warning}</Alert>)}
                <Link to={`/recordings/${encodeURIComponent(candidate.recordingId)}/show`}>Inspect Catalog Recording</Link></>}
            </TableCell>
            <TableCell><Accordion disableGutters>
              <AccordionSummary>Match evidence and location {row.media.id}</AccordionSummary>
              <AccordionDetails>
                <Typography variant="body2">{row.media.isrc ?? 'No ISRC'}; {row.media.durationSeconds ?? '?'} s</Typography>
                <Typography variant="body2">{row.media.fingerprintKey ? 'Stored acoustic fingerprint available (exact matches only).' : 'No stored audio fingerprint.'}</Typography>
                <Typography variant="body2" sx={{ overflowWrap: 'anywhere' }}>{row.media.path}</Typography>
                {(candidate ? [candidate] : row.candidates).map(c => <Box key={c.recordingId}>
                  <Typography>{c.title} {c.mix}: {c.status}</Typography>
                  {c.evidence.map((e, i) => <Typography variant="body2" key={i}>{e}</Typography>)}
                  {c.blockers.map((b, i) => <Alert severity="warning" key={i}>{b}</Alert>)}
                </Box>)}
              </AccordionDetails>
            </Accordion></TableCell>
          </TableRow>
        })}</TableBody>
      </Table>
    </Box>
    {plan && !visible.length && <Typography>No files in this page match the chosen view.</Typography>}
    {!frozen && <Button variant="contained" disabled={disabled || !valid || !plan} onClick={() => void action(async signal => {
      if (!plan) return
      const verified = await mediaClient.prepareMatches(plan, selections, signal)
      pendingReview.current = verified
      sessionStorage.setItem(recoveryKey, verified.id)
      setReview(verified)
      const outcome = await mediaClient.acceptMatches(verified, signal)
      saved({ ...verified, outcome })
    })}>Accept selected ({selections.length})</Button>}
    {frozen && <Stack spacing={1}>
      <Alert severity="warning">Batch {review.id} was prepared. An interrupted acceptance may already have saved it. Check its saved outcome or retry this exact batch; do not start a replacement blindly.</Alert>
      <Accordion>
        <AccordionSummary>Frozen selections — {review.selections.length} files, {review.verifiedMediaIds.length} verified copies</AccordionSummary>
        <AccordionDetails>{review.selections.map(selection => {
          const file = review.plan.files.find(f => f.media.id === selection.mediaId)
          return <Typography key={selection.mediaId}>{file?.media.title ?? selection.mediaId}: {selection.recordingId ?? 'No Recording link'}
            {selection.classification ? `; classification: ${selection.classification}` : ''}</Typography>
        })}</AccordionDetails>
      </Accordion>
      <Button disabled={busy} onClick={() => void action(async signal => {
        const current = await mediaClient.matchReview(review.id, signal)
        setReview(current); saved(current)
        if (!current.outcome) setError('This batch has not been accepted. Retry it, or discard the unaccepted review.')
      })}>Check saved outcome</Button>
      <Button disabled={busy} onClick={() => void action(async signal => {
        const outcome = await mediaClient.acceptMatches(review, signal)
        saved({ ...review, outcome })
      })}>Retry same selected batch</Button>
      <Button disabled={busy} onClick={() => void action(async signal => {
        const current = await mediaClient.matchReview(review.id, signal)
        if (current.outcome) { saved(current); return }
        pendingReview.current = undefined; sessionStorage.removeItem(recoveryKey)
        setReview(undefined); setRevision(value => value + 1)
      })}>Check then discard unaccepted review</Button>
    </Stack>}
    {manual && <ManualMedia media={manual.media} recoveryId={manual.recoveryId} onSaved={manualSaved} onClose={() => {
      setManual(undefined); onBusy(!!pendingReview.current)
    }} />}
  </Stack>
}
