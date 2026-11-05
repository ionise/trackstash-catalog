import { useEffect, useState } from 'react'
import type { ReactNode } from 'react'
import { Alert, Box, Button, Checkbox, Dialog, DialogActions, DialogContent, DialogTitle, FormControlLabel, MenuItem, Stack, TextField, Typography } from '@mui/material'
import { useLogoutIfAccessDenied } from 'react-admin'
import { artistClient } from './catalog'
import type { ArtistProfile as Profile, ArtistProfileSnapshot } from './catalog'

const kinds = ['official', 'bandcamp', 'discogs', 'musicbrainz', 'resident_advisor', 'beatport', 'social', 'other']
export function ArtistProfilePanel({ id, version, children }: {
  id: string; version: string; children: (state: { snapshot?: ArtistProfileSnapshot; error: string; busy: boolean; edit: () => void; editBiography: () => void; reload: () => void; toggleFavourite: () => void }, dialog: ReactNode) => ReactNode
}) {
  const [snapshot, setSnapshot] = useState<ArtistProfileSnapshot>()
  const [draft, setDraft] = useState<Profile>()
  const [open, setOpen] = useState(false)
  const [biographyOnly, setBiographyOnly] = useState(false)
  const [busy, setBusy] = useState(false)
  const [loadedRequest, setLoadedRequest] = useState<string>()
  const [error, setError] = useState('')
  const [stale, setStale] = useState(false)
  const [reload, setReload] = useState(0)
  const requestKey = `${version}:${reload}`
  const loading = !open && loadedRequest !== requestKey
  const logout = useLogoutIfAccessDenied()
  useEffect(() => {
    if (open) return
    const controller = new AbortController()
    void artistClient.profile(id, controller.signal).then(result => {
      if (!controller.signal.aborted) { setSnapshot(result); setError(''); setStale(false) }
    }).catch(failure => {
      if (!controller.signal.aborted) { setError(failure instanceof Error ? failure.message : 'Artist profile could not be loaded.'); void logout(failure) }
    }).finally(() => {
      if (!controller.signal.aborted) setLoadedRequest(requestKey)
    })
    return () => controller.abort()
  }, [id, requestKey, open, logout])
  function close() { setOpen(false); setReload(value => value + 1) }
  const dirty = JSON.stringify(draft) !== JSON.stringify(snapshot?.profile)
  function patch(update: Partial<Profile>) { setDraft(current => current ? { ...current, ...update } : current) }
  async function save() {
    if (!snapshot || !draft) return
    setBusy(true); setError('')
    try {
      const result = await artistClient.saveProfile(id, snapshot.etag, draft)
      setSnapshot(result); setDraft(result.profile); close()
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : 'Artist profile save failed.')
      if (failure && typeof failure === 'object' && 'status' in failure && failure.status === 412) setStale(true)
      await logout(failure)
    } finally { setBusy(false) }
  }
  async function reloadDraft() {
    setBusy(true); setError('')
    try { const result = await artistClient.profile(id); setSnapshot(result); setDraft(result.profile); setStale(false) }
    catch (failure) { setError(failure instanceof Error ? failure.message : 'Artist profile reload failed.'); await logout(failure) }
    finally { setBusy(false) }
  }
  async function toggleFavourite() {
    if (!snapshot || open || busy || stale) return
    setBusy(true); setError('')
    try { setSnapshot(await artistClient.saveProfile(id, snapshot.etag, { ...snapshot.profile, favourite: !snapshot.profile.favourite })) }
    catch (failure) {
      setError(failure instanceof Error ? failure.message : 'Favourite update failed.')
      if (failure && typeof failure === 'object' && 'status' in failure && failure.status === 412) setStale(true)
      await logout(failure)
    }
    finally { setBusy(false) }
  }
  const dialog = <Dialog open={open} fullWidth maxWidth="md" onClose={() => { if (!busy) close() }}>
    <DialogTitle>{biographyOnly ? 'Edit biography' : 'Edit Artist profile'}</DialogTitle>
    <DialogContent><Stack spacing={2} sx={{ mt: 1 }}>
      {error && <Alert severity="error">{error}</Alert>}
      <Typography>Manual biography and links only. Name/identity edits and automatic biography fetching are not included.</Typography>
      {draft && <Box component="fieldset" disabled={busy} sx={{ border: 0, p: 0, m: 0, display: 'flex', flexDirection: 'column', gap: 2, minWidth: 0 }}>
        <TextField label="Biography" multiline minRows={5} value={draft.biography ?? ''} onChange={e => patch({ biography: e.target.value || null })} slotProps={{ htmlInput: { maxLength: 12000 } }} />
        <TextField label="Biography source URL" value={draft.biographySourceUrl ?? ''} onChange={e => patch({ biographySourceUrl: e.target.value || null })} slotProps={{ htmlInput: { maxLength: 2048 } }} />
        {!biographyOnly && <>
        <FormControlLabel control={<Checkbox checked={draft.favourite} onChange={e => patch({ favourite: e.target.checked })} />} label="Favourite" />
        <TextField label="Private notes" multiline minRows={3} value={draft.notes ?? ''} onChange={e => patch({ notes: e.target.value || null })} slotProps={{ htmlInput: { maxLength: 12000 } }} />
        <TextField select label="Review status" value={draft.reviewStatus} onChange={e => patch({ reviewStatus: e.target.value })}>
          {['unreviewed', 'in_progress', 'reviewed'].map(status => <MenuItem key={status} value={status}>{status.replaceAll('_', ' ')}</MenuItem>)}
        </TextField>
        <Typography component="h3" variant="h6">Web and social links</Typography>
        {draft.links.map((link, index) => <Stack spacing={1} key={index}>
          <TextField select label={`Link ${index + 1} kind`} value={link.kind} onChange={e => patch({ links: draft.links.map((l, i) => i === index ? { ...l, kind: e.target.value } : l) })}>
            {kinds.map(kind => <MenuItem key={kind} value={kind}>{kind.replaceAll('_', ' ')}</MenuItem>)}
          </TextField>
          {(['url', 'title', 'sourceUrl'] as const).map(field => <TextField key={field} label={`Link ${index + 1} ${field}`} value={link[field] ?? ''}
            slotProps={{ htmlInput: { maxLength: field === 'title' ? 200 : 2048 } }}
            onChange={e => patch({ links: draft.links.map((l, i) => i === index ? { ...l, [field]: field === 'url' ? e.target.value : e.target.value || null } : l) })} />)}
          <Button onClick={() => patch({ links: draft.links.filter((_, i) => i !== index) })}>Remove link {index + 1}</Button>
        </Stack>)}
        <Button disabled={draft.links.length >= 100 || busy} onClick={() => patch({ links: [...draft.links, { kind: 'official', url: '', title: null, sourceUrl: null }] })}>Add link</Button>
        </>}
      </Box>}
      <Button disabled={busy} onClick={() => void reloadDraft()}>Reload profile (discard unsaved changes)</Button>
    </Stack></DialogContent>
    <DialogActions><Button disabled={busy} onClick={close}>Cancel profile edits</Button>
      <Button variant="contained" disabled={busy || stale || !dirty || !snapshot || !draft} onClick={() => void save()}>{biographyOnly ? 'Save biography' : 'Save Artist profile'}</Button></DialogActions>
  </Dialog>
  return <>{children({ snapshot, error, busy: busy || loading, edit: () => {
    if (snapshot) { setDraft(structuredClone(snapshot.profile)); setBiographyOnly(false); setOpen(true); setStale(false); setError('') }
  }, editBiography: () => {
    if (snapshot) { setDraft(structuredClone(snapshot.profile)); setBiographyOnly(true); setOpen(true); setStale(false); setError('') }
  }, reload: () => setReload(value => value + 1), toggleFavourite: () => void toggleFavourite() }, dialog)}</>
}
