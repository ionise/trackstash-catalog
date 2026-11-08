import { useEffect, useRef, useState } from 'react'
import { Alert, Button, Checkbox, FormControlLabel, LinearProgress, Stack, TextField, Typography } from '@mui/material'
import { Title, useAuthenticated, useLogoutIfAccessDenied } from 'react-admin'
import { Link } from 'react-router-dom'
import { mediaClient } from './catalog'
import type { MediaLibrarySetting, MediaLibrarySettings } from './catalog'

export function MediaLibraries() {
  const { isPending } = useAuthenticated()
  const logout = useLogoutIfAccessDenied()
  const [settings, setSettings] = useState<MediaLibrarySettings>()
  const [draft, setDraft] = useState<MediaLibrarySetting[]>([])
  const [confirmed, setConfirmed] = useState(false)
  const [error, setError] = useState('')
  const [saved, setSaved] = useState(false)
  const [busy, setBusy] = useState(false)
  const [revision, setRevision] = useState(0)
  const pending = useRef<AbortController | undefined>(undefined)
  useEffect(() => () => pending.current?.abort(), [])
  useEffect(() => {
    if (isPending) return
    const controller = new AbortController()
    void mediaClient.libraries(controller.signal).then(result => {
      if (!controller.signal.aborted) { setSettings(result); setDraft(result.libraries) }
    }).catch(failure => { if (!controller.signal.aborted) {
      setError(failure instanceof Error ? failure.message : 'Library settings failed to load.'); void logout(failure)
    } })
    return () => controller.abort()
  }, [isPending, revision, logout])
  function change(id: string, changes: Partial<MediaLibrarySetting>) {
    setDraft(draft.map(l => l.id === id ? { ...l, ...changes } : l)); setConfirmed(false); setSaved(false)
  }
  async function save() {
    if (!settings || pending.current) return
    const controller = new AbortController()
    pending.current = controller; setBusy(true); setError(''); setSaved(false)
    try {
      const result = await mediaClient.saveLibraries(settings, draft, confirmed, controller.signal)
      if (!controller.signal.aborted) { setSettings(result); setDraft(result.libraries); setConfirmed(false); setSaved(true) }
    } catch (failure) {
      if (!controller.signal.aborted) { setError(failure instanceof Error ? failure.message : 'Library settings could not be saved.'); void logout(failure) }
    } finally { if (!controller.signal.aborted) { pending.current = undefined; setBusy(false) } }
  }
  return <Stack spacing={2} sx={{ p: 3, minWidth: 0 }}>
    <Title title="Libraries" /><Typography variant="h4" component="h1">Media libraries</Typography>
    <Alert severity="info">Choose any library names and absolute directory locations. Names are independent of drive names. Saving takes effect without an API restart, but does not rescan, move files or change scanner roots. After relocating files, update the scanner separately.</Alert>
    <Alert severity="warning">Enabled roots allow the local API to read media and sidecars during explicit verification. Only add directories you intend TrackStash to access. Unmounted libraries can be configured; availability is checked when verifying files.</Alert>
    {error && <Alert severity="error">{error}</Alert>}
    {saved && <Alert severity="success">Library settings saved. <Link to="/media">Open Media</Link> to see the updated names and locations.</Alert>}
    {(busy || !settings && !error) && <LinearProgress />}
    <Button disabled={busy} onClick={() => {
      setSettings(undefined); setError(''); setConfirmed(false); setSaved(false); setRevision(value => value + 1)
    }}>Reload saved settings (discard edits)</Button>
    {settings && <>
      {draft.map((library, index) => <Stack key={library.id} component="section" aria-label={`Library ${index + 1}`} spacing={1}
        sx={{ border: 1, borderColor: 'divider', p: 2 }}>
        <TextField label={`Library ${index + 1} name`} value={library.name} disabled={busy}
          slotProps={{ htmlInput: { maxLength: 100 } }} onChange={event => change(library.id, { name: event.target.value })} />
        <TextField label={`Library ${index + 1} root directory`} value={library.root} disabled={busy}
          slotProps={{ htmlInput: { maxLength: 1024 } }} onChange={event => change(library.id, { root: event.target.value })} />
        <FormControlLabel label={`Enable library ${index + 1}`} control={<Checkbox checked={library.enabled} disabled={busy}
          onChange={event => change(library.id, { enabled: event.target.checked })} />} />
        <Typography variant="caption" sx={{ overflowWrap: 'anywhere' }}>Stable ID: {library.id}. Disable unused libraries; existing identities and history are retained.</Typography>
      </Stack>)}
      {!draft.length && <Typography>No libraries configured. Add a library to start browsing scanner inventory.</Typography>}
      <Button disabled={busy || draft.length >= 32} onClick={() => {
        setDraft([...draft, { id: crypto.randomUUID(), name: '', root: '', enabled: true }]); setConfirmed(false); setSaved(false)
      }}>Add library</Button>
      <FormControlLabel label="I approve these library names and directory access settings"
        control={<Checkbox checked={confirmed} disabled={busy} onChange={event => setConfirmed(event.target.checked)} />} />
      <Button variant="contained" disabled={busy || !confirmed || draft.some(l => !l.name.trim() || !l.root.trim())}
        onClick={() => void save()}>Save library settings</Button>
    </>}
  </Stack>
}
