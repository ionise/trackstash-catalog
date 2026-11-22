import { useEffect, useRef, useState } from 'react'
import { Alert, Box, Button, Checkbox, FormControlLabel, LinearProgress, MenuItem, Stack, TextField, Typography } from '@mui/material'
import { HttpError, useLogoutIfAccessDenied } from 'react-admin'
import { mediaClient } from './catalog'
import type { EnrichmentLookup, SonoImportIdentity, SonoImportIntent, SonoImportPreview } from './catalog'

const recoveryKey = 'trackstash.sonoImport.pending'
type Recovery = { id: string; etag: string; intent: SonoImportIntent }

export function MediaSonoVaultImport({ release, disabled, onBusy, onAdmitted }: {
  release?: EnrichmentLookup; disabled: boolean; onBusy: (busy: boolean) => void; onAdmitted: (releaseId: string) => void
}) {
  const [review, setReview] = useState<SonoImportPreview>()
  const [label, setLabel] = useState('')
  const [artists, setArtists] = useState<Record<string, string>>({})
  const [recordings, setRecordings] = useState<Record<string, string>>({})
  const [allowIncomplete, setAllowIncomplete] = useState(false)
  const [confirmDistinct, setConfirmDistinct] = useState(false)
  const [recovery, setRecovery] = useState<Recovery | undefined>(() => {
    const saved = sessionStorage.getItem(recoveryKey)
    return saved ? JSON.parse(saved) as Recovery : undefined
  })
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const pending = useRef<AbortController | undefined>(undefined)
  const logout = useLogoutIfAccessDenied()
  useEffect(() => () => pending.current?.abort(), [])
  async function action(task: (signal: AbortSignal) => Promise<void>) {
    if (pending.current) return
    const controller = new AbortController()
    pending.current = controller; setBusy(true); onBusy(true); setError('')
    try { await task(controller.signal) }
    catch (failure) {
      if (!controller.signal.aborted) {
        setError(failure instanceof Error ? failure.message : 'SonoVault import failed.')
        void logout(failure)
      }
    } finally {
      if (pending.current === controller) { pending.current = undefined; setBusy(false); onBusy(false) }
    }
  }
  async function save(saved: Recovery, signal: AbortSignal) {
    sessionStorage.setItem(recoveryKey, JSON.stringify(saved)); setRecovery(saved)
    let result
    try { result = await mediaClient.applySonoImport(saved.id, saved.etag, saved.intent, signal) }
    catch (failure) {
      if (failure instanceof HttpError && failure.status >= 400 && failure.status < 500 && failure.status !== 429) {
        sessionStorage.removeItem(recoveryKey); setRecovery(undefined)
      }
      throw failure
    }
    if (signal.aborted) return
    sessionStorage.removeItem(recoveryKey); setRecovery(undefined); onAdmitted(result.releaseId)
  }
  const candidate = release?.candidates[0]
  const locked = disabled || busy || !!recovery
  const identityChoice = (identity: SonoImportIdentity, selected: Record<string, string>,
    change: (value: Record<string, string>) => void, kind: string) => {
    const value = selected[identity.id] ?? ''
    return <TextField select fullWidth key={identity.id} label={`${kind}: ${identity.name} (${identity.id})`}
      value={value} disabled={locked || !!identity.ownerId} onChange={event => change({ ...selected, [identity.id]: event.target.value })}>
      {!identity.ownerId && identity.candidates.length > 0 && <MenuItem value="">Choose an existing identity</MenuItem>}
      {!identity.ownerId && identity.candidates.length === 0 && <MenuItem value="new">Create new {kind}</MenuItem>}
      {identity.ownerId && !identity.candidates.some(c => c.id === identity.ownerId) &&
        <MenuItem value={identity.ownerId}>Already linked: {identity.ownerId}</MenuItem>}
      {identity.candidates.map(item => <MenuItem key={item.id} value={item.id}>
        {item.name ?? 'Untitled'} {item.mixName ?? ''} {item.isrc ?? ''} ({item.id})
      </MenuItem>)}
    </TextField>
  }
  const complete = review && (review.labels.length === 0 || !!label) &&
    review.artists.every(item => !!artists[item.id]) && review.recordings.every(item => !!recordings[item.id]) &&
    (review.warnings.length === 0 || allowIncomplete) && (review.releaseCandidates.length === 0 || confirmDistinct)
  return <Box component="section" aria-label="SonoVault Release admission">
    {error && <Alert severity="error">{error}</Alert>}
    {busy && <LinearProgress aria-label="Preparing SonoVault admission" />}
    {recovery && <Alert severity="warning">The previous admission may have succeeded. Retry the same operation before starting another.
      <Button disabled={busy || disabled} onClick={() => void action(signal => save(recovery, signal))}>Retry SonoVault admission</Button>
      <Button disabled={busy || disabled} onClick={() => { sessionStorage.removeItem(recoveryKey); setRecovery(undefined) }}>
        Forget recovery pointer (does not undo a saved release)
      </Button>
    </Alert>}
    {!review && candidate && release?.sourceVersion && <Button disabled={locked} onClick={() => void action(async signal => {
      const result = await mediaClient.prepareSonoImport(candidate.candidateId, candidate.editionId, release.sourceVersion!, signal)
      if (signal.aborted) return
      setReview(result); setLabel('')
      setArtists(Object.fromEntries(result.artists.map(item => [item.id, item.ownerId ?? (item.candidates.length ? '' : 'new')])))
      setRecordings(Object.fromEntries(result.recordings.map(item => [item.id, item.ownerId ?? (item.candidates.length ? '' : 'new')])))
    })}>Review SonoVault admission</Button>}
    {review && <Stack spacing={1} sx={{ mt: 1 }}>
      <Typography variant="h6">Review {review.candidate.metadata.name}</Typography>
      <Alert severity="info">Import and media assignment are separate saves. Existing Recordings are reused without overwriting their metadata.</Alert>
      {review.existingReleaseId && <Alert severity="info">This SonoVault identity is already in the Catalog.
        <Button disabled={locked} onClick={() => onAdmitted(review.existingReleaseId!)}>Use existing Release</Button>
      </Alert>}
      {review.blockers.map(blocker => <Alert severity="error" key={blocker}>{blocker}</Alert>)}
      {review.releaseCandidates.length > 0 && <Alert severity="warning">Releases with this title already exist:
        {review.releaseCandidates.map(item => <Box key={item.id}>{item.name ?? 'Untitled'} ({item.id})
          <Button disabled={locked} onClick={() => onAdmitted(item.id)}>Use this existing Release</Button>
        </Box>)}
        <FormControlLabel control={<Checkbox checked={confirmDistinct} disabled={locked}
          onChange={event => setConfirmDistinct(event.target.checked)} />} label="Create a distinct Release despite these candidates" />
      </Alert>}
      {review.warnings.map(warning => <Alert severity="warning" key={warning}>{warning}</Alert>)}
      {!!review.warnings.length && <FormControlLabel control={<Checkbox checked={allowIncomplete} disabled={locked}
        onChange={event => setAllowIncomplete(event.target.checked)} />} label="I accept that this tracklist may be incomplete or group-level" />}
      {review.labels.length > 0 ? <TextField select label={`Label: ${review.candidate.metadata.labelName ?? 'Unknown'}`} value={label}
        disabled={locked} onChange={event => setLabel(event.target.value)}>
        <MenuItem value="">Choose a Label</MenuItem>
        {review.labels.map(item => <MenuItem key={item.id} value={item.id}>{item.name} ({item.id})</MenuItem>)}
      </TextField> : <Typography>New Label: {review.candidate.metadata.labelName ?? 'not supplied'}</Typography>}
      {review.artists.map(item => identityChoice(item, artists, setArtists, 'Artist'))}
      {review.recordings.map(item => identityChoice(item, recordings, setRecordings, 'Recording'))}
      <Button variant="contained" disabled={locked || !complete || !!review.blockers.length || !!review.existingReleaseId}
        onClick={() => {
          const intent: SonoImportIntent = {
            labelId: label || null,
            artists: review.artists.map(item => ({ providerId: item.id, canonicalId: artists[item.id] === 'new' ? null : artists[item.id] })),
            recordings: review.recordings.map(item => ({ providerId: item.id, canonicalId: recordings[item.id] === 'new' ? null : recordings[item.id] })),
            allowIncomplete, confirmDistinctRelease: confirmDistinct,
          }
          void action(signal => save({ id: review.id, etag: review.etag, intent }, signal))
        }}>Create reviewed SonoVault Release</Button>
    </Stack>}
  </Box>
}
