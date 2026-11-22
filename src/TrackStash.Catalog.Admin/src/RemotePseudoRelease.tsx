import { useEffect, useRef, useState } from 'react'
import { Alert, Box, Button, LinearProgress, MenuItem, Stack, TextField, Typography } from '@mui/material'
import { HttpError, useLogoutIfAccessDenied } from 'react-admin'
import { Link } from 'react-router-dom'
import { artistClient, remoteInventoryClient } from './catalog'
import type { CatalogArtist, PseudoReleaseOutcome, RemoteInventoryPage, RemoteMatchProposal, RemotePseudoRequest } from './catalog'

const recoveryKey = 'trackstash.remotePseudo.pending'
type Row = { item: RemoteInventoryPage['items'][number]; proposal: RemoteMatchProposal }
type Choice = { mode: string; title: string; mix: string; artist: string; artistId: string }

export function RemotePseudoRelease({ rows, disabled, onBusy, onSaved }: {
  rows: Row[]; disabled: boolean; onBusy: (busy: boolean) => void; onSaved: () => void
}) {
  const [title, setTitle] = useState(rows[0]?.item.release ?? rows[0]?.item.album ?? '')
  const [catalogNumber, setCatalogNumber] = useState('NO CAT')
  const [choices, setChoices] = useState<Choice[]>(rows.map(({ item }) => ({
    mode: 'new', title: item.title ?? '', mix: item.mix ?? '', artist: item.artist ?? '', artistId: '',
  })))
  const [artists, setArtists] = useState<Record<number, CatalogArtist[]>>({})
  const [recovery, setRecovery] = useState<RemotePseudoRequest | undefined>(() => {
    const saved = sessionStorage.getItem(recoveryKey)
    return saved ? JSON.parse(saved) as RemotePseudoRequest : undefined
  })
  const [outcome, setOutcome] = useState<PseudoReleaseOutcome>()
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const pending = useRef<AbortController | undefined>(undefined)
  const logout = useLogoutIfAccessDenied()
  useEffect(() => () => pending.current?.abort(), [])
  function update(index: number, patch: Partial<Choice>) {
    setChoices(current => current.map((choice, i) => i === index ? { ...choice, ...patch } : choice))
  }
  async function action(task: (signal: AbortSignal) => Promise<void>) {
    if (pending.current) return
    const controller = new AbortController()
    pending.current = controller; setBusy(true); onBusy(true); setError('')
    try { await task(controller.signal) }
    catch (failure) {
      if (!controller.signal.aborted) {
        setError(failure instanceof Error ? failure.message : 'Device pseudo-release creation failed.')
        void logout(failure)
      }
    } finally { if (pending.current === controller) { pending.current = undefined; setBusy(false); onBusy(false) } }
  }
  async function save(request: RemotePseudoRequest, signal: AbortSignal) {
    sessionStorage.setItem(recoveryKey, JSON.stringify(request)); setRecovery(request)
    let result: PseudoReleaseOutcome
    try { result = await remoteInventoryClient.createPseudoRelease(request, signal) }
    catch (failure) {
      if (failure instanceof HttpError && failure.status >= 400 && failure.status < 500 && failure.status !== 429) {
        sessionStorage.removeItem(recoveryKey); setRecovery(undefined)
      }
      throw failure
    }
    if (signal.aborted) return
    sessionStorage.removeItem(recoveryKey); setRecovery(undefined); setOutcome(result); onSaved()
  }
  const locked = disabled || busy || !!recovery || !!outcome
  const valid = !!title.trim() && rows.length > 0 && choices.every(choice =>
    (choice.mode !== 'new' || !!choice.title.trim()) &&
    (!choice.mode.startsWith('copy:') || !choices[Number(choice.mode.slice(5)) - 1]?.mode.startsWith('copy:')))
  return <Box component="section" aria-label="Create device pseudo release">
    <Alert severity="warning">Uses uploaded scanner snapshots only. Device files and audio are not freshly verified.
      The release and all selected links are saved together under Not on Label.</Alert>
    {error && <Alert severity="error">{error}</Alert>}
    {busy && <LinearProgress aria-label="Creating device pseudo release" />}
    {recovery && <Alert severity="warning">A previous request may have saved. Retry the same operation before creating another.
      <Button disabled={busy || disabled} onClick={() => void action(signal => save(recovery, signal))}>Retry device pseudo release</Button>
      <Button disabled={busy || disabled} onClick={() => { sessionStorage.removeItem(recoveryKey); setRecovery(undefined) }}>
        Forget recovery pointer (does not undo a saved release)
      </Button>
    </Alert>}
    {outcome && <Alert severity="success">{outcome.mediaFiles} uploaded records linked. <Link
      to={`/releases/${encodeURIComponent(outcome.releaseId)}/show`}>View pseudo release</Link></Alert>}
    <Stack spacing={2} sx={{ mt: 2 }}>
      <TextField label="Device pseudo release title" value={title} disabled={locked}
        onChange={event => setTitle(event.target.value)} slotProps={{ htmlInput: { maxLength: 200 } }} />
      <TextField label="Device pseudo catalog number" value={catalogNumber} disabled={locked}
        onChange={event => setCatalogNumber(event.target.value)} slotProps={{ htmlInput: { maxLength: 200 } }} />
      {rows.map(({ item, proposal }, index) => {
        const choice = choices[index]
        return <Box key={`${item.ownerId}/${item.deviceId}/${item.mediaFileId}`}>
          <Typography>{item.title ?? 'Untitled'} — {item.artist ?? 'Unknown Artist'} ({item.deviceId}, media {item.mediaFileId})</Typography>
          <TextField select fullWidth label={`Device pseudo track ${index + 1}`} value={choice.mode} disabled={locked}
            onChange={event => update(index, { mode: event.target.value })}>
            <MenuItem value="new">Create a new Recording</MenuItem>
            {proposal.candidates.map(candidate => <MenuItem key={candidate.recordingId} value={`recording:${candidate.recordingId}`}>
              Existing: {candidate.title} {candidate.mix} — {candidate.status} ({candidate.recordingId})
            </MenuItem>)}
            {choices.slice(0, index).map((prior, i) => !prior.mode.startsWith('copy:') &&
              <MenuItem key={i} value={`copy:${i + 1}`}>Another copy of track {i + 1}</MenuItem>)}
          </TextField>
          {choice.mode.startsWith('recording:') && <Alert severity="warning">The existing Recording is reused without changing its metadata.
            Confirm the version and Artist match this file.</Alert>}
          {choice.mode === 'new' && <Stack spacing={1} sx={{ mt: 1 }}>
            <TextField label={`Device track title ${index + 1}`} value={choice.title} disabled={locked}
              onChange={event => update(index, { title: event.target.value })} slotProps={{ htmlInput: { maxLength: 200 } }} />
            <TextField label={`Device track mix ${index + 1}`} value={choice.mix} disabled={locked}
              onChange={event => update(index, { mix: event.target.value })} slotProps={{ htmlInput: { maxLength: 200 } }} />
            <TextField label={`Device track Artist ${index + 1}`} value={choice.artist} disabled={locked || !!choice.artistId}
              onChange={event => update(index, { artist: event.target.value })} slotProps={{ htmlInput: { maxLength: 200 } }}
              helperText="A new Artist is created if supplied. Identical new names in this batch share one identity." />
            <Button disabled={locked || !choice.artist.trim()} onClick={() => void action(async signal => {
              const found = await artistClient.list(new URLSearchParams({ q: choice.artist.trim(), page: '1', pageSize: '25' }), signal)
              setArtists(current => ({ ...current, [index]: found.items }))
            })}>Find existing Artist for device track {index + 1}</Button>
            {artists[index] && <TextField select label={`Device Artist choice ${index + 1}`} value={choice.artistId} disabled={locked}
              onChange={event => update(index, { artistId: event.target.value })}>
              <MenuItem value="">Create new Artist / no Artist</MenuItem>
              {artists[index].map(artist => <MenuItem key={artist.id} value={artist.id}>{artist.name} ({artist.id})</MenuItem>)}
            </TextField>}
          </Stack>}
        </Box>
      })}
      <Button variant="contained" disabled={locked || !valid} onClick={() => {
        const request: RemotePseudoRequest = { operationId: crypto.randomUUID().replaceAll('-', ''), title: title.trim(),
          catalogNumber: catalogNumber.trim() || null, items: rows.map(({ proposal }, index) => {
            const choice = choices[index]
            return { key: proposal.key, version: proposal.version,
              recordingId: choice.mode.startsWith('recording:') ? choice.mode.slice(10) : null,
              copyOfIndex: choice.mode.startsWith('copy:') ? Number(choice.mode.slice(5)) : null,
              title: choice.mode === 'new' ? choice.title.trim() : null,
              mix: choice.mode === 'new' ? choice.mix.trim() || null : null,
              artistId: choice.mode === 'new' ? choice.artistId || null : null,
              newArtist: choice.mode === 'new' && !choice.artistId ? choice.artist.trim() || null : null }
          }) }
        void action(signal => save(request, signal))
      }}>Create device pseudo release and link records</Button>
    </Stack>
  </Box>
}
