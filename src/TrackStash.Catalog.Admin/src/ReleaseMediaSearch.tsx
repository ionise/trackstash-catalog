import { useEffect, useRef, useState } from 'react'
import { Accordion, AccordionDetails, AccordionSummary, Alert, Button, Checkbox, LinearProgress, MenuItem, Stack, Table, TableBody, TableCell, TableHead, TableRow, TextField, Typography } from '@mui/material'
import ExpandMoreIcon from '@mui/icons-material/ExpandMore'
import { useLogoutIfAccessDenied } from 'react-admin'
import { remoteInventoryClient, trackClient } from './catalog'
import type { CatalogTrack, RemoteInventoryKey, RemoteInventoryPage, RemoteMatchProposal } from './catalog'

const keyOf = (key: RemoteInventoryKey) => JSON.stringify([key.ownerId, key.deviceId, key.mediaFileId])

export function ReleaseMediaSearch({ id, title, onSaved }: { id: string; title: string | null; onSaved: () => void }) {
  const [open, setOpen] = useState(false)
  return <Accordion expanded={open} onChange={(_, expanded) => setOpen(expanded)}>
    <AccordionSummary expandIcon={<ExpandMoreIcon />}>Find unmatched device media for this release</AccordionSummary>
    <AccordionDetails>{open && <Search id={id} title={title} onSaved={onSaved} />}</AccordionDetails>
  </Accordion>
}

function Search({ id, title, onSaved }: { id: string; title: string | null; onSaved: () => void }) {
  const logout = useLogoutIfAccessDenied()
  const [search, setSearch] = useState((title ?? '').slice(0, 200))
  const [query, setQuery] = useState((title ?? '').slice(0, 200))
  const [device, setDevice] = useState('')
  const [devices, setDevices] = useState<string[]>([])
  const [page, setPage] = useState(1)
  const [revision, setRevision] = useState(0)
  const [tracks, setTracks] = useState<CatalogTrack[]>()
  const [data, setData] = useState<RemoteInventoryPage>()
  const [proposals, setProposals] = useState<RemoteMatchProposal[]>()
  const [selected, setSelected] = useState<Record<string, string>>({})
  const [error, setError] = useState('')
  const [success, setSuccess] = useState('')
  const [busy, setBusy] = useState(false)
  const pending = useRef<AbortController | undefined>(undefined)
  useEffect(() => () => pending.current?.abort(), [])
  useEffect(() => {
    const controller = new AbortController()
    async function load() {
      const missing: CatalogTrack[] = []
      for (let pageNumber = 1; ; pageNumber++) {
        const result = await trackClient.list(new URLSearchParams({
          releaseId: id, media: 'none', sort: 'order', page: String(pageNumber), pageSize: '100',
        }), controller.signal)
        missing.push(...result.items)
        if (pageNumber * result.pageSize >= result.total || !result.items.length) break
      }
      const inventory = await remoteInventoryClient.list(page, query, controller.signal, { device, match: 'unmatched' })
      const suggestions = inventory.items.length
        ? await remoteInventoryClient.proposals(inventory.items.map(({ ownerId, deviceId, mediaFileId }) =>
          ({ ownerId, deviceId, mediaFileId })), controller.signal) : []
      if (inventory.items.some(item => {
        const proposal = suggestions.find(value => keyOf(value.key) === keyOf(item))
        return !proposal || proposal.version !== item.version
      })) throw new Error('Scanner metadata changed while loading; reload the inventory.')
      if (!controller.signal.aborted) {
        setTracks(missing.filter((track, index) => missing.findIndex(other => other.recordingId === track.recordingId) === index))
        setData(inventory); setDevices(inventory.devices); setProposals(suggestions)
      }
    }
    void load().catch(failure => {
      if (!controller.signal.aborted) {
        setError(failure instanceof Error ? failure.message : 'Unmatched media could not be loaded.')
        void logout(failure)
      }
    })
    return () => controller.abort()
  }, [id, page, query, device, revision, logout])

  function changeView(action: () => void) {
    setData(undefined); setTracks(undefined); setProposals(undefined); setSelected({}); setError(''); setSuccess('')
    action()
  }
  async function save() {
    if (!data || !proposals || pending.current) return
    const items = data.items.flatMap(item => {
      const key = { ownerId: item.ownerId, deviceId: item.deviceId, mediaFileId: item.mediaFileId }
      const recordingId = selected[keyOf(key)]
      const proposal = proposals.find(value => keyOf(value.key) === keyOf(key))
      return recordingId && proposal && !proposal.linkedRecordingId
        ? [{ key, recordingId, version: proposal.version }] : []
    })
    if (!items.length) return
    const controller = new AbortController()
    pending.current = controller; setBusy(true); setError(''); setSuccess('')
    try {
      const result = await remoteInventoryClient.match(items, controller.signal)
      if (!controller.signal.aborted) {
        setSelected({}); setData(undefined); setTracks(undefined); setProposals(undefined)
        setSuccess(`${result.saved} files linked; ${result.alreadyLinked} were already linked.`)
        setRevision(value => value + 1); onSaved()
      }
    } catch (failure) {
      if (!controller.signal.aborted) {
        setError(failure instanceof Error ? failure.message : 'Media could not be linked.')
        void logout(failure)
      }
    } finally {
      if (pending.current === controller) { pending.current = undefined; setBusy(false) }
    }
  }
  const selectedCount = Object.keys(selected).length
  const trackIds = new Set(tracks?.map(track => track.recordingId))
  return <Stack spacing={2} sx={{ minWidth: 0 }}>
    <Alert severity="info">Search uploaded scanner records and select a missing recording on this release. Links use historical metadata; files are not freshly verified.</Alert>
    <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2}>
      <TextField label="Search unmatched device media" value={search} disabled={busy}
        onChange={event => setSearch(event.target.value)} slotProps={{ htmlInput: { maxLength: 200 } }} />
      <Button disabled={busy} onClick={() => changeView(() => { setPage(1); setQuery(search); setRevision(value => value + 1) })}>Search media</Button>
      <TextField select label="Device library" value={device} disabled={busy} onChange={event => {
        const value = event.target.value
        changeView(() => { setDevice(value); setPage(1) })
      }}><MenuItem value="">All devices</MenuItem>{devices.map(value => <MenuItem key={value} value={value}>{value}</MenuItem>)}</TextField>
    </Stack>
    {error && <Alert severity="error">{error} <Button disabled={busy} onClick={() => changeView(() => setRevision(value => value + 1))}>Reload</Button></Alert>}
    {success && <Alert severity="success">{success}</Alert>}
    {(!data && !error || busy) && <LinearProgress aria-label="Loading unmatched media" />}
    {tracks && data && proposals && <>
      {!tracks.length && <Alert severity="success">Every catalog recording on this release has linked media, or the release has no catalog tracks.</Alert>}
      <Typography>{data.total} unmatched files; {tracks.length} missing recordings. Page {data.page}. {selectedCount} selected.</Typography>
      <Stack direction="row" spacing={1} useFlexGap sx={{ flexWrap: 'wrap' }}>
        <Button disabled={busy || !tracks.length} onClick={() => {
          const next: Record<string, string> = {}
          for (const proposal of proposals)
            if (!proposal.linkedRecordingId && proposal.suggestedRecordingId && trackIds.has(proposal.suggestedRecordingId))
              next[keyOf(proposal.key)] = proposal.suggestedRecordingId
          setSelected(next)
        }}>Select supported suggestions on this page</Button>
        <Button variant="contained" disabled={busy || !selectedCount} onClick={() => void save()}>Link selected media</Button>
      </Stack>
      <Table size="small" aria-label="Unmatched device media for release">
        <TableHead><TableRow><TableCell>Select</TableCell><TableCell>Scanner media</TableCell><TableCell>Missing release recording</TableCell></TableRow></TableHead>
        <TableBody>{data.items.map(item => {
          const key = keyOf(item)
          const proposal = proposals.find(value => keyOf(value.key) === key)
          const suggestion = proposal?.suggestedRecordingId
          const choice = selected[key] ?? ''
          return <TableRow key={key}>
            <TableCell><Checkbox checked={!!choice} disabled={busy || !suggestion || !trackIds.has(suggestion)}
              slotProps={{ input: { 'aria-label': `Select media ${item.mediaFileId}` } }}
              onChange={event => setSelected(current => {
                const next = { ...current }
                if (event.target.checked && suggestion) next[key] = suggestion
                else delete next[key]
                return next
              })} /></TableCell>
            <TableCell>{item.title ?? 'No title'}<Typography variant="body2">{item.artist ?? 'No artist'}; {item.release ?? item.album ?? 'No release'}</Typography>
              <Typography variant="caption" sx={{ overflowWrap: 'anywhere' }}>{item.deviceId}: {item.path}</Typography></TableCell>
            <TableCell><TextField select label={`Recording for media ${item.mediaFileId}`} value={choice} disabled={busy || !tracks.length || !!proposal?.linkedRecordingId}
              onChange={event => setSelected(current => {
                const next = { ...current }
                if (event.target.value) next[key] = event.target.value
                else delete next[key]
                return next
              })} sx={{ minWidth: 200 }}>
              <MenuItem value="">Not selected</MenuItem>
              {tracks.map(track => <MenuItem key={track.recordingId} value={track.recordingId}>
                {track.title ?? 'Untitled recording'} {track.mixName ?? ''}{track.recordingId === suggestion ? ' — supported suggestion' : ''}
              </MenuItem>)}
            </TextField>
              {proposal?.linkedRecordingId && <Typography variant="caption">Already linked; reload the inventory.</Typography>}
              {suggestion && trackIds.has(suggestion) && <Typography variant="caption" sx={{ display: 'block' }}>
                {proposal?.candidates.find(candidate => candidate.recordingId === suggestion)?.evidence.join('; ')}
              </Typography>}
            </TableCell>
          </TableRow>
        })}</TableBody>
      </Table>
      {!data.items.length && <Typography>No unmatched device media matches this search.</Typography>}
      <Stack direction="row" spacing={1}>
        <Button disabled={busy || page <= 1} onClick={() => changeView(() => setPage(page - 1))}>Previous media</Button>
        <Button disabled={busy || page * data.pageSize >= data.total} onClick={() => changeView(() => setPage(page + 1))}>Next media</Button>
      </Stack>
    </>}
  </Stack>
}
