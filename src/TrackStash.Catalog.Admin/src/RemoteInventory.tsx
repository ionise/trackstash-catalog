import { useEffect, useRef, useState } from 'react'
import { Accordion, AccordionDetails, AccordionSummary, Alert, Box, Button, Checkbox, Dialog, DialogContent, DialogTitle, FormControlLabel, LinearProgress, MenuItem, Stack, Table, TableBody, TableCell, TableHead, TableRow, TextField, Typography } from '@mui/material'
import { Title, useAuthenticated, useLogoutIfAccessDenied } from 'react-admin'
import { Link } from 'react-router-dom'
import { beatportClient, remoteInventoryClient, trackClient } from './catalog'
import type { BeatportReleaseDetail, RemoteInventoryKey, RemoteInventoryPage, RemoteMatchProposal } from './catalog'
import { MediaMetadata } from './MediaMetadata'
import { MediaProviderSearch } from './MediaProviderSearch'
import { ReleaseImportReview } from './ReleaseImportReview'
import { RemotePseudoRelease } from './RemotePseudoRelease'

const keyOf = (key: RemoteInventoryKey) => JSON.stringify([key.ownerId, key.deviceId, key.mediaFileId])

export function RemoteInventory() {
  const { isPending } = useAuthenticated()
  const logout = useLogoutIfAccessDenied()
  const [search, setSearch] = useState('')
  const [query, setQuery] = useState('')
  const [page, setPage] = useState(1)
  const [device, setDevice] = useState('')
  const [match, setMatch] = useState('')
  const [sort, setSort] = useState('release')
  const [direction, setDirection] = useState('asc')
  const [revision, setRevision] = useState(0)
  const [loaded, setLoaded] = useState<{ viewKey: string;
    data: RemoteInventoryPage; proposals: RemoteMatchProposal[] }>()
  const [selected, setSelected] = useState<Record<string, { id: string; label: string }>>({})
  const [pseudoSelected, setPseudoSelected] = useState<Record<string, boolean>>({})
  const [pseudoOpen, setPseudoOpen] = useState(false)
  const [pseudoBusy, setPseudoBusy] = useState(false)
  const [error, setError] = useState('')
  const [success, setSuccess] = useState('')
  const [busy, setBusy] = useState(false)
  const [bulk, setBulk] = useState<{ page: number; pages: number; saved: number; already: number }>()
  const [picker, setPicker] = useState<string>()
  const [trackQuery, setTrackQuery] = useState('')
  const [tracks, setTracks] = useState<Awaited<ReturnType<typeof trackClient.list>>>()
  const [trackPage, setTrackPage] = useState(1)
  const [discovery, setDiscovery] = useState<{ key: RemoteInventoryKey; version: string; title: string }>()
  const [discoveredRelease, setDiscoveredRelease] = useState<BeatportReleaseDetail>()
  const [importBusy, setImportBusy] = useState(false)
  const [catalogRelease, setCatalogRelease] = useState('')
  const pending = useRef<AbortController | undefined>(undefined)
  const viewKey = JSON.stringify([page, query, revision, device, match, sort, direction])
  const current = loaded?.viewKey === viewKey ? loaded : undefined
  const data = current?.data
  const proposals = current?.proposals
  useEffect(() => () => pending.current?.abort(), [])
  useEffect(() => {
    if (isPending) return
    const controller = new AbortController()
    void remoteInventoryClient.list(page, query, controller.signal, { device, match, sort, order: direction }).then(async result => {
      const suggestions = result.items.length
        ? await remoteInventoryClient.proposals(result.items.map(({ ownerId, deviceId, mediaFileId }) =>
          ({ ownerId, deviceId, mediaFileId })), controller.signal) : []
      if (!controller.signal.aborted) setLoaded({ viewKey, data: result, proposals: suggestions })
    }).catch(failure => {
      if (!controller.signal.aborted) {
        setError(failure instanceof Error ? failure.message : 'Device inventory could not be loaded.')
        void logout(failure)
      }
    })
    return () => controller.abort()
  }, [page, query, viewKey, device, match, sort, direction, isPending, logout])

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
      const keys: RemoteInventoryKey[] = []
      for (;;) {
        const batch = await remoteInventoryClient.list(currentPage, query, signal, { device, match, sort, order: direction })
        const pages = Math.ceil(batch.total / batch.pageSize)
        setBulk({ page: currentPage, pages, saved, already })
        keys.push(...batch.items.map(({ ownerId, deviceId, mediaFileId }) => ({ ownerId, deviceId, mediaFileId })))
        if (currentPage >= pages || !batch.items.length) break
        currentPage++
      }
      const pages = Math.ceil(keys.length / 25)
      for (let offset = 0; offset < keys.length; offset += 25) {
        setBulk({ page: Math.floor(offset / 25) + 1, pages, saved, already })
        const suggestions = await remoteInventoryClient.proposals(keys.slice(offset, offset + 25), signal)
        const matches = suggestions.filter(item => item.suggestedRecordingId && !item.linkedRecordingId)
          .map(item => ({ key: item.key, version: item.version, recordingId: item.suggestedRecordingId! }))
        if (matches.length) {
          const result = await remoteInventoryClient.match(matches, signal)
          saved += result.saved; already += result.alreadyLinked
        }
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
  const pseudoRows = (data?.items ?? []).flatMap(item => {
    const proposal = proposals?.find(value => keyOf(value.key) === keyOf(item))
    return proposal && pseudoSelected[keyOf(proposal.key)] && !proposal.linkedRecordingId ? [{ item, proposal }] : []
  })
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
    <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2}>
      <TextField select label="Device library" value={device} disabled={busy} onChange={event => {
        setDevice(event.target.value); setPage(1); setSelected({}); setError(''); setSuccess('')
      }}><MenuItem value="">All devices</MenuItem>{(data?.devices ?? loaded?.data.devices ?? []).map(value => <MenuItem key={value} value={value}>{value}</MenuItem>)}</TextField>
      <TextField select label="Match status" value={match} disabled={busy} onChange={event => {
        setMatch(event.target.value); setPage(1); setSelected({}); setError(''); setSuccess('')
      }}><MenuItem value="">All files</MenuItem><MenuItem value="unmatched">Unmatched</MenuItem><MenuItem value="matched">Matched</MenuItem></TextField>
      <TextField select label="Sort device inventory" value={sort} disabled={busy} onChange={event => {
        setSort(event.target.value); setPage(1); setSelected({}); setError(''); setSuccess('')
      }}>{['release', 'title', 'artist', 'label', 'device'].map(value => <MenuItem key={value} value={value}>{value}</MenuItem>)}</TextField>
      <TextField select label="Sort direction" value={direction} disabled={busy} onChange={event => {
        setDirection(event.target.value); setPage(1); setSelected({}); setError(''); setSuccess('')
      }}><MenuItem value="asc">Ascending</MenuItem><MenuItem value="desc">Descending</MenuItem></TextField>
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
        <Button disabled={busy || !pseudoRows.length && !sessionStorage.getItem('trackstash.remotePseudo.pending')}
          onClick={() => setPseudoOpen(true)}>Create pseudo release from {pseudoRows.length} selected device files</Button>
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
              })} />
              <FormControlLabel control={<Checkbox checked={!!pseudoSelected[id]} disabled={busy || !!proposal.linkedRecordingId}
                onChange={event => setPseudoSelected(current => ({ ...current, [id]: event.target.checked }))} />}
                label={`Pseudo media ${item.mediaFileId}`} />
            </TableCell>
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
                <Button disabled={busy} onClick={() => {
                  setDiscovery({ key: proposal.key, version: proposal.version, title: item.title ?? '' })
                  setDiscoveredRelease(undefined); setCatalogRelease(''); setError('')
                }}>Find provider metadata for media {item.mediaFileId}</Button>
              </>}</TableCell>
            <TableCell>{item.deviceId}<Typography variant="body2" sx={{ overflowWrap: 'anywhere' }}>{item.path}</Typography>
              <Accordion><AccordionSummary>File metadata {item.mediaFileId}</AccordionSummary>
                <AccordionDetails><MediaMetadata item={item} /></AccordionDetails></Accordion></TableCell>
          </TableRow>
        })}</TableBody>
      </Table></Box>
      {!data.items.length && <Typography>No device inventory matches this search.</Typography>}
      <Stack direction="row" spacing={1}>
        <Button disabled={busy || page === 1} onClick={() => { setError(''); setSelected({}); setPage(page - 1) }}>Previous</Button>
        <Button disabled={busy || page * data.pageSize >= data.total} onClick={() => { setError(''); setSelected({}); setPage(page + 1) }}>Next</Button>
      </Stack>
    </>}
    <Dialog open={pseudoOpen} onClose={pseudoBusy ? undefined : () => setPseudoOpen(false)} fullWidth maxWidth="lg">
      <DialogTitle>Create pseudo release for device media</DialogTitle>
      <DialogContent><RemotePseudoRelease rows={pseudoRows} disabled={busy} onBusy={setPseudoBusy}
        onSaved={() => { setPseudoSelected({}); setRevision(value => value + 1) }} />
        <Button disabled={pseudoBusy} onClick={() => setPseudoOpen(false)}>Close pseudo release</Button>
      </DialogContent>
    </Dialog>
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
    <Dialog open={!!discovery} onClose={busy || importBusy ? undefined : () => setDiscovery(undefined)} fullWidth maxWidth="lg">
      <DialogTitle>Find a Release for device media</DialogTitle>
      <DialogContent><Stack spacing={2} sx={{ pt: 1 }}>
        <Alert severity="info">Uses historical uploaded tags; device audio is not read. Import and media matching are separate saves.</Alert>
        {error && <Alert severity="error">{error}</Alert>}
        {discovery && <MediaProviderSearch source={{ kind: 'remote', key: discovery.key, version: discovery.version }}
          disabled={busy || importBusy} onSonoBusy={setImportBusy}
          onSonoAdmitted={id => { setCatalogRelease(id); setRevision(value => value + 1) }} onBeatportTrack={id => void action(async signal => {
            setDiscoveredRelease(undefined); setCatalogRelease('')
            const detail = await beatportClient.trackRelease(id, signal)
            if (signal.aborted) return
            if (detail.release.catalogReleaseIds.length > 1)
              throw new Error('Several Catalog Releases reference this Beatport identity. Resolve the duplicate identities before admission.')
            if (detail.release.catalogReleaseIds.length === 1) setCatalogRelease(detail.release.catalogReleaseIds[0])
            else {
              const source = await beatportClient.prepareImport(detail.release.id, signal)
              if (!source.sourceCompleteAfter)
                throw new Error(`Beatport metadata remains incomplete: ${source.remainingBlockers.join(', ')}.`)
            }
            if (!signal.aborted) setDiscoveredRelease(detail)
          })} />}
        {busy && <LinearProgress aria-label="Loading provider Release" />}
        {discoveredRelease && <>
          <Typography variant="h6">{discoveredRelease.release.name} — {discoveredRelease.release.label?.name ?? 'Unknown Label'}</Typography>
          {discoveredRelease.trackTotal > discoveredRelease.tracks.length && <Alert severity="warning">
            Showing {discoveredRelease.tracks.length} of {discoveredRelease.trackTotal} Beatport tracks; the list is incomplete.
          </Alert>}
          {discoveredRelease.tracks.map(track => <Typography key={track.id}>{track.name} {track.mixName} — {track.isrc ?? 'No ISRC'}</Typography>)}
          {!catalogRelease && <ReleaseImportReview releaseId={discoveredRelease.release.id} disabled={busy}
            onBusyChange={setImportBusy} onApplied={id => { setImportBusy(false); setCatalogRelease(id); setRevision(value => value + 1) }} />}
        </>}
        {catalogRelease && discovery && <Alert severity="success">Release is in the Catalog. <Link to={`/releases/${encodeURIComponent(catalogRelease)}/show`}>View Release</Link>.
          {' '}Choose the Recording and match this device file; admission did not link or verify it.
          <Button disabled={busy || importBusy} onClick={() => {
            setPicker(keyOf(discovery.key)); setTrackQuery(discovery.title); setTracks(undefined); setTrackPage(1)
            setDiscovery(undefined); setRevision(value => value + 1)
          }}>Choose catalog Recording</Button>
        </Alert>}
        <Button disabled={busy || importBusy} onClick={() => setDiscovery(undefined)}>Close provider lookup</Button>
      </Stack></DialogContent>
    </Dialog>
  </Stack>
}
