import { useEffect, useRef, useState } from 'react'
import { Alert, Box, Button, Checkbox, Dialog, DialogContent, DialogTitle, LinearProgress, MenuItem, Stack, Table, TableBody, TableCell, TableHead, TableRow, TextField, Typography } from '@mui/material'
import { Title, useAuthenticated, useLogoutIfAccessDenied } from 'react-admin'
import { Link } from 'react-router-dom'
import { remoteInventoryClient, trackClient } from './catalog'
import type { RemoteInventoryKey, RemoteInventoryPage, RemoteMatchProposal } from './catalog'

const keyOf = (key: RemoteInventoryKey) => JSON.stringify([key.ownerId, key.deviceId, key.mediaFileId])

export function RemoteInventory() {
  const { isPending } = useAuthenticated()
  const logout = useLogoutIfAccessDenied()
  const [search, setSearch] = useState('')
  const [query, setQuery] = useState('')
  const [page, setPage] = useState(1)
  const [revision, setRevision] = useState(0)
  const [loaded, setLoaded] = useState<{ page: number; query: string; revision: number;
    data: RemoteInventoryPage; proposals: RemoteMatchProposal[] }>()
  const [selected, setSelected] = useState<Record<string, { id: string; label: string }>>({})
  const [error, setError] = useState('')
  const [success, setSuccess] = useState('')
  const [busy, setBusy] = useState(false)
  const [bulk, setBulk] = useState<{ page: number; pages: number; saved: number; already: number }>()
  const [picker, setPicker] = useState<string>()
  const [trackQuery, setTrackQuery] = useState('')
  const [tracks, setTracks] = useState<Awaited<ReturnType<typeof trackClient.list>>>()
  const [trackPage, setTrackPage] = useState(1)
  const pending = useRef<AbortController | undefined>(undefined)
  const current = loaded?.page === page && loaded.query === query && loaded.revision === revision ? loaded : undefined
  const data = current?.data
  const proposals = current?.proposals
  useEffect(() => () => pending.current?.abort(), [])
  useEffect(() => {
    if (isPending) return
    const controller = new AbortController()
    void remoteInventoryClient.list(page, query, controller.signal).then(async result => {
      const suggestions = result.items.length
        ? await remoteInventoryClient.proposals(result.items.map(({ ownerId, deviceId, mediaFileId }) =>
          ({ ownerId, deviceId, mediaFileId })), controller.signal) : []
      if (!controller.signal.aborted) setLoaded({ page, query, revision, data: result, proposals: suggestions })
    }).catch(failure => {
      if (!controller.signal.aborted) {
        setError(failure instanceof Error ? failure.message : 'Device inventory could not be loaded.')
        void logout(failure)
      }
    })
    return () => controller.abort()
  }, [page, query, revision, isPending, logout])

  async function action(task: (signal: AbortSignal) => Promise<void>) {
    if (pending.current) return
    const controller = new AbortController()
    pending.current = controller; setBusy(true); setError('')
    try { await task(controller.signal) }
    catch (failure) {
      if (!controller.signal.aborted) {
        setError(failure instanceof Error ? failure.message : 'Matching failed.')
        void logout(failure)
      }
    } finally {
      if (pending.current === controller) { pending.current = undefined; setBusy(false) }
    }
  }
  function searchTracks(nextPage: number) {
    void action(async signal => {
      const result = await trackClient.list(new URLSearchParams({
        q: trackQuery, page: String(nextPage), pageSize: '25', groupBy: 'recording', sort: 'title',
      }), signal)
      setTracks(result); setTrackPage(nextPage)
    })
  }
  async function matchSupported(signal: AbortSignal) {
    let currentPage = 1
    let saved = 0
    let already = 0
    let finished = false
    setBulk({ page: 1, pages: 0, saved: 0, already: 0 })
    try {
      for (;;) {
        const batch = await remoteInventoryClient.list(currentPage, query, signal)
        const pages = Math.ceil(batch.total / batch.pageSize)
        setBulk({ page: currentPage, pages, saved, already })
        if (batch.items.length) {
          const suggestions = await remoteInventoryClient.proposals(batch.items.map(({ ownerId, deviceId, mediaFileId }) =>
            ({ ownerId, deviceId, mediaFileId })), signal)
          const matches = suggestions.filter(item => item.suggestedRecordingId && !item.linkedRecordingId)
            .map(item => ({ key: item.key, version: item.version, recordingId: item.suggestedRecordingId! }))
          if (matches.length) {
            const result = await remoteInventoryClient.match(matches, signal)
            saved += result.saved; already += result.alreadyLinked
          }
        }
        if (currentPage >= pages || !batch.items.length) break
        currentPage++
      }
      finished = true
    } finally {
      setSuccess(finished ? `${saved} files matched; ${already} were already linked.`
        : `Matching stopped. ${saved} links were confirmed; the last batch may also have saved. Retrying is safe.`)
      setBulk(undefined); setSelected({})
      setRevision(value => value + 1)
    }
  }
  const chosen = proposals?.filter(proposal => selected[keyOf(proposal.key)] && !proposal.linkedRecordingId) ?? []
  return <Stack spacing={2} sx={{ p: 3, minWidth: 0 }}>
    <Title title="Device inventory" />
    <Typography variant="h4" component="h1">Device inventory and matching</Typography>
    <Link to="/media">Server-local media</Link>
    <Alert severity="info">Match using uploaded scan data, even when the device is offline. Files are not freshly verified.
      Select suggestions or choose a catalog Recording, then save them together. All catalog administrators can view this inventory.</Alert>
    <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2}>
      <TextField label="Search title, artist, label or release" value={search}
        slotProps={{ htmlInput: { maxLength: 200 } }} onChange={event => setSearch(event.target.value)} />
      <Button disabled={busy} onClick={() => { setPage(1); setQuery(search); setSelected({}); setSuccess(''); setError('') }}>Search</Button>
    </Stack>
    {error && <Alert severity="error">{error} <Button onClick={() => { setError(''); setRevision(value => value + 1) }}>Reload</Button></Alert>}
    {success && <Alert severity="success">{success}</Alert>}
    {(!data && !error || busy) && <LinearProgress aria-label="Loading device inventory" />}
    {bulk && <Stack spacing={1}>
      <Typography>Matching page {bulk.page} of {bulk.pages || '…'}; {bulk.saved} links saved.</Typography>
      <Button onClick={() => pending.current?.abort()}>Stop matching</Button>
    </Stack>}
    {data && proposals && <>
      <Typography>{data.total} files. Page {data.page}. {chosen.length} selected on this page.</Typography>
      <Stack direction="row" spacing={1} useFlexGap sx={{ flexWrap: 'wrap' }}>
        <Button disabled={busy || data.total === 0} onClick={() => {
          setSuccess('')
          void action(matchSupported)
        }}>Match supported suggestions across all results</Button>
        <Button disabled={busy} onClick={() => {
          const next: typeof selected = {}
          for (const proposal of proposals) {
            const candidate = proposal.candidates.find(item => item.recordingId === proposal.suggestedRecordingId)
            if (candidate && !proposal.linkedRecordingId)
              next[keyOf(proposal.key)] = { id: candidate.recordingId, label: `${candidate.title ?? 'Untitled'} ${candidate.mix ?? ''}` }
          }
          setSelected(next)
        }}>Select supported suggestions on this page</Button>
        <Button disabled={busy || !chosen.length} onClick={() => setSelected({})}>Clear selection</Button>
        <Button variant="contained" disabled={busy || !chosen.length} onClick={() => {
          setSuccess('')
          void action(async signal => {
            const result = await remoteInventoryClient.match(chosen.map(proposal => ({
              key: proposal.key, version: proposal.version, recordingId: selected[keyOf(proposal.key)].id,
            })), signal)
            setSuccess(`${result.saved} files matched; ${result.alreadyLinked} were already linked.`)
            setSelected({})
            setRevision(value => value + 1)
          })
        }}>Match selected</Button>
      </Stack>
      <Box sx={{ width: '100%', overflowX: 'auto', contain: 'inline-size' }}><Table size="small" aria-label="Device inventory">
        <TableHead><TableRow><TableCell>Select</TableCell><TableCell>Track / Artist</TableCell><TableCell>Label / Release</TableCell><TableCell>Catalog Recording</TableCell><TableCell>Device / Path</TableCell></TableRow></TableHead>
        <TableBody>{data.items.map(item => {
          const id = keyOf(item)
          const proposal = proposals.find(value => keyOf(value.key) === id)
          if (!proposal) return null
          const choice = selected[id]
          const suggestion = proposal.candidates.find(candidate => candidate.recordingId === proposal.suggestedRecordingId)
          const candidate = proposal.candidates.find(value => value.recordingId === (choice?.id ?? suggestion?.recordingId))
          return <TableRow key={id}>
            <TableCell><Checkbox slotProps={{ input: { 'aria-label': `Select media ${item.mediaFileId}` } }} checked={!!choice}
              disabled={busy || !!proposal.linkedRecordingId || !suggestion && !choice}
              onChange={event => setSelected(current => {
                const next = { ...current }
                if (event.target.checked && suggestion) next[id] = { id: suggestion.recordingId, label: `${suggestion.title ?? 'Untitled'} ${suggestion.mix ?? ''}` }
                else delete next[id]
                return next
              })} /></TableCell>
            <TableCell>{item.title ?? 'No title'}<Typography variant="body2">{item.artist ?? 'No artist'}</Typography></TableCell>
            <TableCell>{item.label ?? 'Unknown label'}<Typography variant="body2">{item.release ?? item.album ?? 'Unknown release'}</Typography></TableCell>
            <TableCell>{proposal.linkedRecordingId
              ? <Link to={`/recordings/${encodeURIComponent(proposal.linkedRecordingId)}/show`}>Already matched</Link>
              : <>
                <TextField select label={`Recording for media ${item.mediaFileId}`} value={choice?.id ?? suggestion?.recordingId ?? ''}
                  disabled={busy} onChange={event => {
                    const found = proposal.candidates.find(value => value.recordingId === event.target.value)
                    if (found) setSelected(current => ({ ...current, [id]: { id: found.recordingId, label: `${found.title ?? 'Untitled'} ${found.mix ?? ''}` } }))
                    else setSelected(current => { const next = { ...current }; delete next[id]; return next })
                  }} sx={{ minWidth: 200 }}>
                  <MenuItem value="">No Recording selected</MenuItem>
                  {choice && !proposal.candidates.some(value => value.recordingId === choice.id) && <MenuItem value={choice.id}>{choice.label}</MenuItem>}
                  {proposal.candidates.map(value => <MenuItem key={value.recordingId} value={value.recordingId}>
                    {value.title ?? 'Untitled'} {value.mix ?? ''} — {value.status}
                  </MenuItem>)}
                </TextField>
                {candidate?.blockers.map(blocker => <Typography key={blocker} variant="body2" color="warning.main">{blocker}</Typography>)}
                <Button disabled={busy} onClick={() => {
                  setPicker(id); setTrackQuery(item.title ?? ''); setTracks(undefined); setTrackPage(1)
                }}>Find another Recording</Button>
              </>}</TableCell>
            <TableCell>{item.deviceId}<Typography variant="body2" sx={{ overflowWrap: 'anywhere' }}>{item.path}</Typography></TableCell>
          </TableRow>
        })}</TableBody>
      </Table></Box>
      {!data.items.length && <Typography>No device inventory matches this search.</Typography>}
      <Stack direction="row" spacing={1}>
        <Button disabled={busy || page === 1} onClick={() => { setError(''); setSelected({}); setPage(page - 1) }}>Previous</Button>
        <Button disabled={busy || page * data.pageSize >= data.total} onClick={() => { setError(''); setSelected({}); setPage(page + 1) }}>Next</Button>
      </Stack>
    </>}
    <Dialog open={!!picker} onClose={busy ? undefined : () => setPicker(undefined)} fullWidth maxWidth="md">
      <DialogTitle>Choose a catalog Recording</DialogTitle>
      <DialogContent><Stack spacing={2} sx={{ pt: 1 }}>
        <TextField label="Search Catalog Recordings" value={trackQuery} onChange={event => setTrackQuery(event.target.value)} />
        <Button disabled={busy} onClick={() => searchTracks(1)}>Search Recordings</Button>
        {tracks && <>
          <Typography>{tracks.total} Recordings found. Page {trackPage}.</Typography>
          {tracks.items.map(track => <Button key={track.recordingId} disabled={busy} onClick={() => {
            if (picker) setSelected(current => ({ ...current, [picker]: { id: track.recordingId, label: `${track.title ?? 'Untitled'} ${track.mixName ?? ''}` } }))
            setPicker(undefined)
          }}>Choose {track.title ?? 'Untitled'} {track.mixName ?? ''} — {track.recordingCredits.filter(credit => credit.role === 'primary')
            .map(credit => credit.creditedName ?? credit.artistName).filter(Boolean).join(', ') || 'Unknown Artist'}</Button>)}
          <Stack direction="row">
            <Button disabled={busy || trackPage <= 1} onClick={() => searchTracks(trackPage - 1)}>Previous Recordings</Button>
            <Button disabled={busy || trackPage * tracks.pageSize >= tracks.total} onClick={() => searchTracks(trackPage + 1)}>Next Recordings</Button>
          </Stack>
        </>}
        <Button disabled={busy} onClick={() => setPicker(undefined)}>Cancel</Button>
      </Stack></DialogContent>
    </Dialog>
  </Stack>
}
