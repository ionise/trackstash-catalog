import { useEffect, useState } from 'react'
import { Alert, Button, Checkbox, Dialog, DialogActions, DialogContent, DialogTitle, FormControlLabel, MenuItem, Stack, TextField, Typography } from '@mui/material'
import { useDataProvider, useLogoutIfAccessDenied, useRefresh } from 'react-admin'
import { groupClient, relationshipsClient } from './catalog'
import type { LabelRecord, LabelRelationships, RelatedLabel } from './catalog'

export function IncomingLabelRelationships({ labelId }: { labelId: string }) {
  const [open, setOpen] = useState(false)
  const refresh = useRefresh()
  return <>
    <Button onClick={() => setOpen(true)}>Manage child imprints / incoming relationships</Button>
    {open && <IncomingEditor labelId={labelId} onClose={() => setOpen(false)} onSaved={() => { setOpen(false); refresh() }} />}
  </>
}

function IncomingEditor({ labelId, onClose, onSaved }: { labelId: string; onClose: () => void; onSaved: () => void }) {
  const provider = useDataProvider()
  const logout = useLogoutIfAccessDenied()
  const [graph, setGraph] = useState<LabelRelationships>()
  const [related, setRelated] = useState<RelatedLabel>()
  const [id, setId] = useState<string | null>(null)
  const [type, setType] = useState<'imprint_of' | 'distributed_by'>('imprint_of')
  const [source, setSource] = useState('manual')
  const [url, setUrl] = useState('')
  const [notes, setNotes] = useState('')
  const [search, setSearch] = useState('')
  const [candidates, setCandidates] = useState<LabelRecord[]>([])
  const [page, setPage] = useState(1)
  const [total, setTotal] = useState(0)
  const [busy, setBusy] = useState(false)
  const [stale, setStale] = useState(false)
  const [error, setError] = useState('')
  const [confirmed, setConfirmed] = useState(false)
  const [remove, setRemove] = useState(false)
  async function execute(action: () => Promise<void>) {
    setBusy(true); setError('')
    try { await action() }
    catch (failure) {
      setError(failure instanceof Error ? failure.message : 'Relationship request failed.')
      if (failure && typeof failure === 'object' && 'status' in failure && failure.status === 412) setStale(true)
      await logout(failure)
    } finally { setBusy(false) }
  }
  useEffect(() => {
    const controller = new AbortController()
    void relationshipsClient.get(labelId, controller.signal).then(setGraph).catch(failure => {
      if (!controller.signal.aborted) { setError(failure instanceof Error ? failure.message : 'Relationships could not be loaded.'); void logout(failure) }
    })
    return () => controller.abort()
  }, [labelId, logout])
  function reset() { setConfirmed(false); setRemove(false) }
  function reload() {
    setGraph(undefined); setRelated(undefined); setCandidates([]); setId(null); reset()
    setType('imprint_of'); setSource('manual'); setUrl(''); setNotes(''); setPage(1); setTotal(0)
    void execute(async () => { setGraph(await relationshipsClient.get(labelId)); setStale(false) })
  }
  function find(nextPage: number) {
    void execute(async () => {
      const result = await provider.getList<LabelRecord>('labels', {
        pagination: { page: nextPage, perPage: 10 }, sort: { field: 'name', order: 'ASC' }, filter: { q: search },
      })
      setCandidates(result.data); setPage(nextPage); setTotal(result.total ?? 0)
    })
  }
  function save() {
    if (!related || !graph) return
    void execute(async () => {
      await relationshipsClient.edge(labelId, graph.etag, {
        id, relatedLabelId: related.id, incoming: true, type, source, sourceUrl: url || null, notes: notes || null,
        targetVersion: related.version, remove,
      })
      onSaved()
    })
  }
  return <Dialog open fullWidth maxWidth="md" onClose={() => { if (!busy) onClose() }}>
    <DialogTitle>Manage child imprints and incoming relationships</DialogTitle>
    <DialogContent sx={{ overflowWrap: 'anywhere' }}><Stack spacing={2} sx={{ pt: 1 }}>
      <Typography>Add a child from its parent's page, or a distributed label from its distributor's page. One directed relationship is stored; unrelated edges are preserved.</Typography>
      {error && <Alert severity="error">{error}</Alert>}
      <Button disabled={busy} onClick={reload}>Reload incoming relationships</Button>
      {graph?.items.filter(e => e.to.id === labelId).map(edge => <Button key={edge.id} disabled={busy || stale} onClick={() => {
        setId(edge.id); setRelated(edge.from); setType(edge.type); setSource(edge.source); setUrl(edge.sourceUrl ?? ''); setNotes(edge.notes ?? ''); reset()
      }}>Edit {edge.from.name || edge.from.id} ({edge.type === 'imprint_of' ? 'child imprint' : 'distributed label'})</Button>)}
      <TextField label="Find child or distributed label" value={search} disabled={busy || stale || !graph} onChange={e => setSearch(e.target.value)} />
      <Button disabled={busy || stale || !graph} onClick={() => find(1)}>Search incoming labels</Button>
      {candidates.filter(c => c.id !== labelId).map(candidate => <Button key={candidate.id} disabled={busy || stale} onClick={() => {
        reset(); setId(null); setType('imprint_of'); setSource('manual'); setUrl(''); setNotes('')
        void execute(async () => setRelated(await groupClient.label(candidate.id)))
      }}>Choose child {candidate.name} ({candidate.id})</Button>)}
      <Stack direction="row"><Button disabled={busy || stale || page <= 1} onClick={() => find(page - 1)}>Previous incoming labels</Button>
        <Typography>Page {page}; {total} results</Typography><Button disabled={busy || stale || page * 10 >= total} onClick={() => find(page + 1)}>Next incoming labels</Button></Stack>
      {related && <>
        <Typography>Related label: {related.name || related.id}</Typography>
        <TextField select label="Incoming relationship" value={type} disabled={busy || stale || remove} onChange={e => {
          if (e.target.value === 'imprint_of' || e.target.value === 'distributed_by') { setType(e.target.value); setConfirmed(false) }
        }}><MenuItem value="imprint_of">Has imprint (child label)</MenuItem><MenuItem value="distributed_by">Distributes label</MenuItem></TextField>
        <TextField label="Incoming source" required value={source} disabled={busy || stale || remove} slotProps={{ htmlInput: { maxLength: 200 } }} onChange={e => { setSource(e.target.value); setConfirmed(false) }} />
        <TextField label="Incoming evidence URL" value={url} disabled={busy || stale || remove} onChange={e => { setUrl(e.target.value); setConfirmed(false) }} />
        <TextField label="Incoming notes" value={notes} multiline disabled={busy || stale || remove} slotProps={{ htmlInput: { maxLength: 2000 } }} onChange={e => { setNotes(e.target.value); setConfirmed(false) }} />
        {id && <FormControlLabel control={<Checkbox checked={remove} disabled={busy || stale} onChange={e => { setRemove(e.target.checked); setConfirmed(false) }} />} label="Remove this incoming relationship" />}
      </>}
      <FormControlLabel control={<Checkbox checked={confirmed} disabled={busy || stale || !graph || !related} onChange={e => setConfirmed(e.target.checked)} />} label="I have reviewed this incoming relationship and any removal." />
    </Stack></DialogContent>
    <DialogActions><Button disabled={busy} onClick={onClose}>Cancel</Button>
      <Button disabled={busy || stale || !graph || !related || !confirmed || !source.trim()} onClick={save}>Save incoming relationship</Button></DialogActions>
  </Dialog>
}
