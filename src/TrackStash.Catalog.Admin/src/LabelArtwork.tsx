import { useEffect, useRef, useState } from 'react'
import {
  Alert, Box, Button, Checkbox, Dialog, DialogActions, DialogContent, DialogTitle,
  FormControlLabel, MenuItem, Stack, TextField, Typography,
} from '@mui/material'
import { useLogoutIfAccessDenied, useRecordContext, useRefresh } from 'react-admin'
import { artworkClient } from './catalog'
import type { ArtworkGallery, ArtworkItem, LabelRecord } from './catalog'

export function ArtworkImage({ assetId, caption }: { assetId: string; caption: string }) {
  return <AssetImage key={assetId} assetId={assetId} caption={caption} />
}

function AssetImage({ assetId, caption }: { assetId: string; caption: string }) {
  const [url, setUrl] = useState('')
  const [error, setError] = useState('')
  const logout = useLogoutIfAccessDenied()
  useEffect(() => {
    const abort = new AbortController()
    let objectUrl: string | undefined
    void artworkClient.image(assetId, false, abort.signal).then(blob => {
      if (abort.signal.aborted) return
      objectUrl = URL.createObjectURL(blob)
      setUrl(objectUrl)
    }).catch(failure => {
      if (abort.signal.aborted) return
      setError(failure instanceof Error ? failure.message : 'Artwork could not be loaded.')
      void logout(failure)
    })
    return () => { abort.abort(); if (objectUrl) URL.revokeObjectURL(objectUrl) }
  }, [assetId, logout])
  return error ? <Alert severity="error">{error}</Alert> : url
    ? <Box component="img" src={url} alt={caption} sx={{ width: 160, height: 160, objectFit: 'contain', borderRadius: 1, bgcolor: 'action.hover' }} />
    : <Typography>Loading artwork...</Typography>
}

export function LabelArtwork() {
  const record = useRecordContext<LabelRecord>()
  return record ? <LabelArtworkPanel key={record.id} id={record.id} name={record.name} /> : null
}

function LabelArtworkPanel({ id, name }: { id: string; name: string | null }) {
  const refresh = useRefresh()
  const logout = useLogoutIfAccessDenied()
  const [gallery, setGallery] = useState<ArtworkGallery>()
  const [draft, setDraft] = useState<ArtworkItem[]>([])
  const [open, setOpen] = useState(false)
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)
  const [file, setFile] = useState<File>()
  const fileInput = useRef<HTMLInputElement>(null)
  const [role, setRole] = useState('logo')
  const [caption, setCaption] = useState('')
  const [sourceUrl, setSourceUrl] = useState('')
  const [attribution, setAttribution] = useState('')
  const [rights, setRights] = useState('')
  const [preferred, setPreferred] = useState(false)
  const [replacement, setReplacement] = useState<string>()
  const uploadSection = useRef<HTMLDivElement>(null)
  useEffect(() => {
    const abort = new AbortController()
    void artworkClient.get(id, abort.signal).then(result => {
      if (!abort.signal.aborted) { setGallery(result); setDraft(result.items) }
    }).catch(failure => {
      if (!abort.signal.aborted) {
        setError(failure instanceof Error ? failure.message : 'Gallery could not be loaded.')
        void logout(failure)
      }
    })
    return () => abort.abort()
  }, [id, logout])

  const dirty = JSON.stringify(draft) !== JSON.stringify(gallery?.items ?? [])
  async function execute(action: () => Promise<void>) {
    setBusy(true)
    setError('')
    try { await action() }
    catch (failure) {
      setError(failure instanceof Error ? failure.message : 'Artwork request failed.')
      await logout(failure)
    } finally { setBusy(false) }
  }
  function accept(result: ArtworkGallery) { setGallery(result); setDraft(result.items); setReplacement(undefined); refresh() }
  function edit(index: number, patch: Partial<ArtworkItem>) {
    setDraft(items => items.map((item, i) => i === index ? { ...item, ...patch } : item))
  }
  function move(index: number, offset: number) {
    setDraft(items => {
      const next = [...items]
      ;[next[index], next[index + offset]] = [next[index + offset], next[index]]
      return next
    })
  }
  function upload() {
    if (!file || !gallery) return
    if (file.size > 10 * 1024 * 1024) { setError('Choose a JPEG or PNG of at most 10 MiB.'); return }
    const form = new FormData()
    form.set('file', file); form.set('role', role); form.set('caption', caption)
    form.set('sourceUrl', sourceUrl); form.set('attribution', attribution)
    form.set('usageRights', rights); form.set('isPreferred', String(preferred))
    if (replacement) form.set('replaceEntryId', replacement)
    void execute(async () => {
      accept(await artworkClient.upload(id!, gallery.etag, form))
      setFile(undefined); setCaption(''); setSourceUrl(''); setAttribution(''); setRights(''); setPreferred(false)
      setReplacement(undefined)
      if (fileInput.current) fileInput.current.value = ''
    })
  }

  return <Box sx={{ width: '100%', py: 2 }}>
    <Stack direction="row" spacing={2} sx={{ alignItems: 'center' }}>
      <Typography variant="h6">Artwork</Typography>
      <Button disabled={busy} onClick={() => { setOpen(true); setDraft(gallery?.items ?? []); setReplacement(undefined); setError('') }}>Manage artwork</Button>
    </Stack>
    {!open && error && <Alert severity="error">{error}</Alert>}
    {gallery?.items.length === 0 && <Typography color="text.secondary">No artwork recorded.</Typography>}
    <Stack direction="row" useFlexGap sx={{ flexWrap: 'wrap' }} spacing={2}>
      {gallery?.items.map(item => <Box key={item.id}>
        <ArtworkImage assetId={item.assetId} caption={item.caption || `${name} ${item.role}`} />
        <Typography>{item.caption || item.role}{item.isPreferred ? ' (preferred)' : ''}</Typography>
      </Box>)}
    </Stack>
    <Dialog open={open} onClose={() => { if (!busy) setOpen(false) }} fullWidth maxWidth="md">
      <DialogTitle>Label artwork and gallery</DialogTitle>
      <DialogContent>
        <Stack spacing={2} sx={{ pt: 1 }}>
          {error && <Alert severity="error">{error}</Alert>}
          <Alert severity="info">Uploads are saved immediately. Gallery edits are saved separately. Removing an entry detaches it; stored images and history are retained. Source and rights belong to the immutable upload.</Alert>
          <Button disabled={busy} onClick={() => void execute(async () => { accept(await artworkClient.get(id)); setReplacement(undefined) })}>Reload artwork (discard unsaved gallery changes)</Button>
          {draft.map((item, index) => <Stack key={item.id} spacing={1} sx={{ p: 2, border: 1, borderColor: 'divider', borderRadius: 1 }}>
            <ArtworkImage assetId={item.assetId} caption={item.caption || `Artwork ${index + 1}`} />
            <Typography>Image {index + 1}: {item.originalFilename || item.assetId}</Typography>
            <TextField select label={`Role for image ${index + 1}`} value={item.role} disabled={busy}
              onChange={event => edit(index, { role: event.target.value })}>
              <MenuItem value="logo">Logo</MenuItem><MenuItem value="image">Gallery image</MenuItem>
            </TextField>
            <TextField label={`Caption for image ${index + 1}`} value={item.caption ?? ''} disabled={busy}
              onChange={event => edit(index, { caption: event.target.value || null })} slotProps={{ htmlInput: { maxLength: 1000 } }} />
            <Typography>Source: {item.sourceUrl || 'Not recorded'}</Typography>
            <Typography>Attribution: {item.attribution || 'Not recorded'}</Typography>
            <Typography>Usage rights: {item.usageRights}</Typography>
            <Typography>{item.width ?? '?'} x {item.height ?? '?'} pixels; {item.mediaType}</Typography>
            <Stack direction="row" useFlexGap sx={{ flexWrap: 'wrap' }} spacing={1}>
              <Button disabled={busy || item.isPreferred} onClick={() => setDraft(items => items.map(i => ({ ...i, isPreferred: i.id === item.id })))}>
                {item.isPreferred ? 'Preferred image' : `Prefer image ${index + 1}`}
              </Button>
              {item.isPreferred && <Button disabled={busy} onClick={() => edit(index, { isPreferred: false })}>Clear preference</Button>}
              <Button disabled={busy || index === 0} onClick={() => move(index, -1)}>Move image {index + 1} earlier</Button>
              <Button disabled={busy || index === draft.length - 1} onClick={() => move(index, 1)}>Move image {index + 1} later</Button>
              <Button color="warning" disabled={busy} onClick={() => setDraft(items => items.filter(i => i.id !== item.id))}>Remove image {index + 1}</Button>
              <Button disabled={busy || dirty} onClick={() => {
                setReplacement(item.id); setRole(item.role); setCaption(item.caption ?? '')
                setPreferred(item.isPreferred); setRights(''); setSourceUrl(''); setAttribution('')
                uploadSection.current?.scrollIntoView({ behavior: 'smooth' })
              }}>Replace image {index + 1}</Button>
            </Stack>
          </Stack>)}
          <Button variant="contained" disabled={busy || !gallery || !dirty} onClick={() => void execute(async () => accept(await artworkClient.save(id, gallery!.etag, draft)))}>
            Save gallery changes
          </Button>
          <Box ref={uploadSection}><Typography variant="h6">{replacement ? 'Replace artwork' : 'Upload artwork'}</Typography></Box>
          {replacement && <Alert severity="info" action={<Button onClick={() => setReplacement(undefined)} disabled={busy}>Cancel replacement</Button>}>
            The selected entry will keep its gallery position and receive a new asset. Its old image remains in history. Record source and rights for the replacement.
          </Alert>}
          {dirty && <Alert severity="warning">Save or reload gallery changes before uploading.</Alert>}
          <Box component="form" onSubmit={event => { event.preventDefault(); upload() }}>
            <Stack spacing={2}>
              <Typography>JPEG or PNG, static, up to 10 MiB and 25 megapixels; neither side may exceed 16384 pixels. Uploading does not establish permission to reuse an image.</Typography>
              <input aria-label="Artwork file" type="file" accept="image/jpeg,image/png" disabled={busy || dirty}
                ref={fileInput} onChange={event => setFile(event.target.files?.[0])} />
              <TextField select label="Upload role" value={role} disabled={busy || dirty} onChange={event => setRole(event.target.value)}>
                <MenuItem value="logo">Logo</MenuItem><MenuItem value="image">Gallery image</MenuItem>
              </TextField>
              <TextField label="Upload caption" value={caption} disabled={busy || dirty} onChange={event => setCaption(event.target.value)} />
              <TextField label="Artwork source URL" type="url" value={sourceUrl} disabled={busy || dirty} onChange={event => setSourceUrl(event.target.value)} />
              <TextField label="Artwork attribution" value={attribution} disabled={busy || dirty} onChange={event => setAttribution(event.target.value)} />
              <TextField label="Usage rights" required value={rights} disabled={busy || dirty} onChange={event => setRights(event.target.value)}
                helperText="Record permission/licence or uncertainty explicitly. This is not a licence verification service." />
              <FormControlLabel control={<Checkbox checked={preferred} disabled={busy || dirty} onChange={event => setPreferred(event.target.checked)} />} label="Prefer this upload" />
              <Button type="submit" variant="contained" disabled={busy || dirty || !file || !rights.trim() || !gallery}>Upload image</Button>
            </Stack>
          </Box>
        </Stack>
      </DialogContent>
      <DialogActions><Button disabled={busy} onClick={() => setOpen(false)}>Close artwork</Button></DialogActions>
    </Dialog>
  </Box>
}
