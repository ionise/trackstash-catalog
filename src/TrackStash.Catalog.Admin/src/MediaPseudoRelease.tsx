import { useEffect, useRef, useState } from 'react'
import { Alert, Box, Button, LinearProgress, MenuItem, Stack, TextField, Typography } from '@mui/material'
import { HttpError, useLogoutIfAccessDenied } from 'react-admin'
import { Link } from 'react-router-dom'
import { artistClient, mediaClient } from './catalog'
import type { CatalogArtist, MediaInventoryItem, MediaMatchPlan, PseudoReleaseOutcome, PseudoReleaseRequest } from './catalog'

const recoveryKey = 'trackstash.pseudoRelease.pending'
type Choice = { mediaId: number; mode: string; title: string; mix: string; artist: string; artistId: string }

export function MediaPseudoRelease({ items, disabled, onBusy, onSaved }: {
  items: MediaInventoryItem[]; disabled: boolean; onBusy: (busy: boolean) => void; onSaved: () => void
}) {
  const [title, setTitle] = useState(items[0].release ?? items[0].album ?? '')
  const [catalogNumber, setCatalogNumber] = useState('NO CAT')
  const [choices, setChoices] = useState<Choice[]>(items.map(item => ({
    mediaId: item.id, mode: 'new', title: item.title ?? '', mix: item.mix ?? '',
    artist: item.artist ?? '', artistId: '',
  })))
  const [plan, setPlan] = useState<MediaMatchPlan>()
  const [artists, setArtists] = useState<Record<number, CatalogArtist[]>>({})
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [outcome, setOutcome] = useState<PseudoReleaseOutcome>()
  const [recovery, setRecovery] = useState<PseudoReleaseRequest | undefined>(() => {
    const saved = sessionStorage.getItem(recoveryKey)
    return saved ? JSON.parse(saved) as PseudoReleaseRequest : undefined
  })
  const pending = useRef<AbortController | undefined>(undefined)
  const logout = useLogoutIfAccessDenied()
  useEffect(() => {
    const controller = new AbortController()
    void mediaClient.matchProposals(items.map(item => item.id), controller.signal).then(result => {
      if (!controller.signal.aborted) setPlan(result)
    }).catch(failure => {
      if (!controller.signal.aborted) {
        setError(failure instanceof Error ? failure.message : 'Media proposals failed.')
        void logout(failure)
      }
    })
    return () => controller.abort()
  }, [items, logout])
  useEffect(() => () => pending.current?.abort(), [])
  function update(id: number, patch: Partial<Choice>) {
    setChoices(current => current.map(choice => choice.mediaId === id ? { ...choice, ...patch } : choice))
  }
  async function action(task: (signal: AbortSignal) => Promise<void>) {
    if (pending.current) return
    const controller = new AbortController()
    pending.current = controller; setBusy(true); onBusy(true); setError('')
    try { await task(controller.signal) }
    catch (failure) {
      if (!controller.signal.aborted) {
        setError(failure instanceof Error ? failure.message : 'Pseudo-release operation failed.')
        void logout(failure)
      }
    } finally {
      if (pending.current === controller) { pending.current = undefined; setBusy(false); onBusy(false) }
    }
  }
  async function save(request: PseudoReleaseRequest, signal: AbortSignal) {
    sessionStorage.setItem(recoveryKey, JSON.stringify(request))
    setRecovery(request)
    let result: PseudoReleaseOutcome
    try { result = await mediaClient.createPseudoRelease(request, signal) }
    catch (failure) {
      if (failure instanceof HttpError && failure.status >= 400 && failure.status < 500) {
        sessionStorage.removeItem(recoveryKey); setRecovery(undefined)
      }
      throw failure
    }
    if (signal.aborted) return
    sessionStorage.removeItem(recoveryKey); setRecovery(undefined); setOutcome(result); onSaved()
  }
  const locked = disabled || busy || !!recovery || !!outcome
  const valid = !!plan && !!title.trim() && choices.every(choice =>
    (choice.mode !== 'new' || !!choice.title.trim()) &&
    (!choice.mode.startsWith('copy:') || choices.some(source =>
      source.mediaId === Number(choice.mode.slice(5)) && !source.mode.startsWith('copy:'))))
  return <Box component="section" aria-label="Create pseudo release">
    <Typography variant="h6">Create pseudo release</Typography>
    <Alert severity="info">The selected files will be verified and linked in one transaction. Choose whether each file is a new Recording, an existing Recording, or another copy. This does not claim an official release or purchase.</Alert>
    {error && <Alert severity="error">{error}</Alert>}
    {busy && <LinearProgress aria-label="Creating pseudo release" />}
    {recovery && <Alert severity="warning">The previous request may have saved. Retry the same operation to recover its outcome before creating another.
      <Button disabled={busy || disabled} onClick={() => void action(signal => save(recovery, signal))}>Retry same operation</Button>
      <Button disabled={busy || disabled} onClick={() => {
        sessionStorage.removeItem(recoveryKey); setRecovery(undefined)
      }}>Forget recovery pointer (does not undo a saved release)</Button>
    </Alert>}
    {outcome && <Alert severity="success">{outcome.mediaFiles} files linked to {outcome.recordings} tracks.
      {' '}<Link to={`/releases/${encodeURIComponent(outcome.releaseId)}/show`}>View pseudo release</Link></Alert>}
    <Stack spacing={2} sx={{ mt: 2 }}>
      <TextField label="Pseudo release title" value={title} disabled={locked} onChange={event => setTitle(event.target.value)}
        slotProps={{ htmlInput: { maxLength: 200 } }} />
      <TextField label="Catalog number" value={catalogNumber} disabled={locked} onChange={event => setCatalogNumber(event.target.value)}
        helperText="Defaults to NO CAT. The label is Not on Label." slotProps={{ htmlInput: { maxLength: 200 } }} />
      {choices.map((choice, index) => {
        const row = plan?.files.find(file => file.media.id === choice.mediaId)
        const copyChoices = choices.slice(0, index).filter(item => !item.mode.startsWith('copy:'))
        return <Box key={choice.mediaId} component="section" aria-label={`Pseudo track ${choice.mediaId}`}>
          <Typography>Media {choice.mediaId}: {row?.media.title ?? 'Untitled'} — {row?.media.artist ?? 'Unknown Artist'}</Typography>
          <TextField select fullWidth label={`Recording for pseudo media ${choice.mediaId}`} value={choice.mode} disabled={locked}
            onChange={event => update(choice.mediaId, { mode: event.target.value })}>
            <MenuItem value="new">Create a new Recording</MenuItem>
            {row?.candidates.map(candidate => <MenuItem key={candidate.recordingId} value={`recording:${candidate.recordingId}`}>
              Existing: {candidate.title} {candidate.mix} — {candidate.status} ({candidate.recordingId})
            </MenuItem>)}
            {copyChoices.map(item => <MenuItem key={item.mediaId} value={`copy:${item.mediaId}`}>Another copy of media {item.mediaId}</MenuItem>)}
          </TextField>
          {choice.mode.startsWith('recording:') && <Alert severity="warning">The existing Recording is reused without changing its metadata. Confirm the title, version and Artist match this file.</Alert>}
          {choice.mode === 'new' && <Stack spacing={1} sx={{ mt: 1 }}>
            <TextField label={`Title for pseudo media ${choice.mediaId}`} value={choice.title} disabled={locked}
              onChange={event => update(choice.mediaId, { title: event.target.value })} slotProps={{ htmlInput: { maxLength: 200 } }} />
            <TextField label={`Mix for pseudo media ${choice.mediaId}`} value={choice.mix} disabled={locked}
              onChange={event => update(choice.mediaId, { mix: event.target.value })} slotProps={{ htmlInput: { maxLength: 200 } }} />
            <TextField label={`Artist for pseudo media ${choice.mediaId}`} value={choice.artist} disabled={locked || !!choice.artistId}
              onChange={event => update(choice.mediaId, { artist: event.target.value })}
              helperText="A new Artist is created if supplied; identical new Artist names in this batch share one identity. Search to reuse an existing Artist." slotProps={{ htmlInput: { maxLength: 200 } }} />
            <Button disabled={locked || !choice.artist.trim()} onClick={() => void action(async signal => {
              const found = await artistClient.list(new URLSearchParams({ q: choice.artist.trim(), page: '1', pageSize: '25' }), signal)
              setArtists(current => ({ ...current, [choice.mediaId]: found.items }))
            })}>Find existing Artist for media {choice.mediaId}</Button>
            {artists[choice.mediaId] && <TextField select label={`Artist choice for pseudo media ${choice.mediaId}`}
              value={choice.artistId} disabled={locked} onChange={event => update(choice.mediaId, { artistId: event.target.value })}>
              <MenuItem value="">Create new Artist / no Artist</MenuItem>
              {artists[choice.mediaId].map(artist => <MenuItem key={artist.id} value={artist.id}>{artist.name} ({artist.id})</MenuItem>)}
            </TextField>}
          </Stack>}
        </Box>
      })}
      <Button variant="contained" disabled={locked || !valid} onClick={() => {
        if (!plan) return
        const request: PseudoReleaseRequest = {
          operationId: crypto.randomUUID().replaceAll('-', ''), planVersion: plan.version, title: title.trim(),
          catalogNumber: catalogNumber.trim() || null,
          items: choices.map(choice => ({
            mediaId: choice.mediaId, recordingId: choice.mode.startsWith('recording:') ? choice.mode.slice(10) : null,
            copyOfMediaId: choice.mode.startsWith('copy:') ? Number(choice.mode.slice(5)) : null,
            title: choice.mode === 'new' ? choice.title.trim() : null,
            mix: choice.mode === 'new' ? choice.mix.trim() || null : null,
            artistId: choice.mode === 'new' ? choice.artistId || null : null,
            newArtist: choice.mode === 'new' && !choice.artistId ? choice.artist.trim() || null : null,
          })),
        }
        void action(signal => save(request, signal))
      }}>Create pseudo release and link media</Button>
    </Stack>
  </Box>
}
