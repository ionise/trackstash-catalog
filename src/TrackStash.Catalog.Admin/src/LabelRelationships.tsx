import { useEffect, useState } from 'react'
import { Alert, Box, Button, Checkbox, Dialog, DialogActions, DialogContent, DialogTitle, FormControlLabel, Link, MenuItem, Stack, TextField, Typography } from '@mui/material'
import { Link as RouterLink, useDataProvider, useLogoutIfAccessDenied, useRecordContext, useRefresh } from 'react-admin'
import { relationshipsClient } from './catalog'
import type { LabelRecord, LabelRelationships as Relationships, RelationshipInput } from './catalog'
import { IncomingLabelRelationships } from './IncomingLabelRelationships'

type Draft = RelationshipInput & { targetName: string }

export function LabelRelationships() {
  const record = useRecordContext<LabelRecord>()
  const provider = useDataProvider()
  const refresh = useRefresh()
  const logout = useLogoutIfAccessDenied()
  const [data, setData] = useState<Relationships>()
  const [draft, setDraft] = useState<Draft[]>([])
  const [etag, setEtag] = useState('')
  const [open, setOpen] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [stale, setStale] = useState(false)
  const [confirmed, setConfirmed] = useState(false)
  const [search, setSearch] = useState('')
  const [candidates, setCandidates] = useState<LabelRecord[]>([])
  const [page, setPage] = useState(1)
  const [total, setTotal] = useState(0)
  const labelId = record?.id ?? ''
  useEffect(() => {
    if (!labelId) return
    const controller = new AbortController()
    void relationshipsClient.get(labelId, controller.signal).then(current => { setData(current); setError('') }).catch(failure => {
      if (!controller.signal.aborted) {
        setError(failure instanceof Error ? failure.message : 'Relationships could not be loaded.')
        void logout(failure)
      }
    })
    return () => controller.abort()
  }, [labelId, record?.version, logout])
  if (!record || !labelId) return null
  async function execute(action: () => Promise<void>) {
    setBusy(true); setError('')
    try { await action() }
    catch (failure) {
      setError(failure instanceof Error ? failure.message : 'Relationship request failed.')
      if (failure && typeof failure === 'object' && 'status' in failure && failure.status === 412) setStale(true)
      await logout(failure)
    } finally { setBusy(false) }
  }
  function load() {
    setEtag(''); setCandidates([]); setDraft([]); setConfirmed(false); setTotal(0); setPage(1)
    void execute(async () => {
      const current = await relationshipsClient.get(labelId)
      setData(current); setEtag(current.etag); setStale(false); setConfirmed(false)
      setDraft(current.items.filter(e => e.from.id === labelId).map(e => ({
        id: e.id, toLabelId: e.to.id, targetName: e.to.name || e.to.id, targetVersion: e.to.version,
        type: e.type, source: e.source, sourceUrl: e.sourceUrl, notes: e.notes,
      })))
    })
  }
  function find(nextPage: number) {
    void execute(async () => {
      const result = await provider.getList<LabelRecord>('labels', {
        pagination: { page: nextPage, perPage: 10 }, sort: { field: 'name', order: 'ASC' }, filter: { q: search },
      })
      setCandidates(result.data); setPage(nextPage); setTotal(result.total ?? 0)
    })
  }
  function change(index: number, patch: Partial<Draft>) {
    setConfirmed(false)
    setDraft(items => items.map((item, position) => position === index ? { ...item, ...patch } : item))
  }
  function save() {
    void execute(async () => {
      const items = draft.map(({ targetName: _targetName, ...item }) => ({
        ...item, sourceUrl: item.sourceUrl || null, notes: item.notes || null,
      }))
      setData(await relationshipsClient.save(labelId, etag, items))
      setOpen(false); refresh()
    })
  }
  return <Box component="section" aria-label="Label relationships">
    <Stack direction="row" sx={{ alignItems: 'center', justifyContent: 'space-between', flexWrap: 'wrap' }}>
      <Typography variant="h6">Label relationships</Typography>
      <Button onClick={() => { setOpen(true); setCandidates([]); setSearch(''); setTotal(0); load() }}>Manage relationships</Button>
    </Stack>
    <IncomingLabelRelationships labelId={labelId} />
    {error && !open && <Alert severity="error">{error}</Alert>}
    {data?.items.length === 0 && <Typography color="text.secondary">No label relationships recorded.</Typography>}
    {data?.items.map(edge => {
      const outgoing = edge.from.id === labelId
      const other = outgoing ? edge.to : edge.from
      const caption = edge.type === 'imprint_of' ? outgoing ? 'Imprint of' : 'Imprint' : outgoing ? 'Distributed by' : 'Distributes'
      return <Box key={edge.id} sx={{ my: 1, overflowWrap: 'anywhere' }}>
        <Typography>{caption}: <Link component={RouterLink} to={`/labels/${encodeURIComponent(other.id)}/show`}>{other.name || other.id}</Link></Typography>
        <Typography variant="body2">Source: {edge.source}{edge.sourceUrl && <> — <Link href={edge.sourceUrl} target="_blank" rel="noopener noreferrer">Evidence</Link></>}</Typography>
        {edge.notes && <Typography variant="body2" sx={{ whiteSpace: 'pre-wrap' }}>{edge.notes}</Typography>}
      </Box>
    })}
    <Dialog open={open} onClose={() => { if (!busy) setOpen(false) }} fullWidth maxWidth="md">
      <DialogTitle>Manage label relationships</DialogTitle>
      <DialogContent sx={{ overflowWrap: 'anywhere' }}>
        <Stack spacing={2} sx={{ pt: 1 }}>
          <Typography>Edit this label’s parents and distributors. Multiple relationships are allowed. Incoming imprints/distribution are shown automatically; manage them using the child imprints / incoming relationships editor or from their originating label.</Typography>
          <Typography variant="body2">Changes affect both labels. No labels are merged, and no evidence is fetched automatically. Dates and territory are not represented yet.</Typography>
          {error && <Alert severity="error">{error}</Alert>}
          <Button disabled={busy} onClick={load}>Reload relationships</Button>
          {draft.map((edge, index) => <Stack key={edge.id ?? `new-${index}`} spacing={1} sx={{ border: 1, borderColor: 'divider', borderRadius: 1, p: 2 }}>
            <Typography>{edge.targetName} ({edge.toLabelId})</Typography>
            <TextField select label={`Relationship ${index + 1}`} value={edge.type} disabled={busy || stale} onChange={event => {
              if (event.target.value === 'imprint_of' || event.target.value === 'distributed_by') change(index, { type: event.target.value })
            }}>
              <MenuItem value="imprint_of">Imprint of (parent label)</MenuItem>
              <MenuItem value="distributed_by">Distributed by</MenuItem>
            </TextField>
            <TextField label={`Source ${index + 1}`} value={edge.source} required disabled={busy || stale}
              slotProps={{ htmlInput: { maxLength: 200 } }} onChange={event => change(index, { source: event.target.value })} />
            <TextField label={`Evidence URL ${index + 1}`} value={edge.sourceUrl ?? ''} disabled={busy || stale}
              onChange={event => change(index, { sourceUrl: event.target.value })} />
            <TextField label={`Relationship notes ${index + 1}`} value={edge.notes ?? ''} multiline disabled={busy || stale}
              slotProps={{ htmlInput: { maxLength: 2000 } }} onChange={event => change(index, { notes: event.target.value })} />
            <Button disabled={busy || stale} onClick={() => { setConfirmed(false); setDraft(items => items.filter((_, position) => position !== index)) }}>Remove relationship {index + 1}</Button>
          </Stack>)}
          <Box component="form" onSubmit={event => { event.preventDefault(); find(1) }}>
            <TextField label="Find related label" value={search} disabled={busy || stale} fullWidth onChange={event => setSearch(event.target.value)} />
            <Button type="submit" disabled={busy || stale}>Search labels</Button>
          </Box>
          {candidates.filter(candidate => candidate.id !== labelId).map(candidate => <Button key={candidate.id} disabled={busy || stale || draft.length >= 100}
            onClick={() => {
              setConfirmed(false)
              setDraft(items => [...items, { id: null, toLabelId: candidate.id, targetName: candidate.name || candidate.id,
                targetVersion: candidate.version, type: 'imprint_of', source: 'manual', sourceUrl: null, notes: null }])
            }}>Add {candidate.name} ({candidate.id})</Button>)}
          <Stack direction="row" spacing={1}>
            <Button disabled={busy || stale || page <= 1} onClick={() => find(page - 1)}>Previous labels</Button>
            <Typography>Page {page}; {total} matching labels</Typography>
            <Button disabled={busy || stale || page * 10 >= total} onClick={() => find(page + 1)}>Next labels</Button>
          </Stack>
          <FormControlLabel control={<Checkbox checked={confirmed} disabled={busy || stale || !etag}
            onChange={event => setConfirmed(event.target.checked)} />} label="I have reviewed these relationships and removals." />
        </Stack>
      </DialogContent>
      <DialogActions>
        <Button disabled={busy} onClick={() => setOpen(false)}>Cancel</Button>
        <Button variant="contained" disabled={busy || stale || !confirmed || !etag || draft.some(e => !e.source.trim())} onClick={save}>Save relationships</Button>
      </DialogActions>
    </Dialog>
  </Box>
}
