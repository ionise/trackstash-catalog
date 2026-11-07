import { useEffect, useRef, useState } from 'react'
import { Alert, Box, Button, Checkbox, FormControlLabel, LinearProgress, MenuItem, Stack, Table, TableBody, TableCell, TableHead, TableRow, TextField, Typography } from '@mui/material'
import { Title, useAuthenticated, useLogoutIfAccessDenied } from 'react-admin'
import { Link } from 'react-router-dom'
import { mediaClient } from './catalog'
import type { MediaLinkReview } from './catalog'

export function MediaInventory() {
  const { isPending } = useAuthenticated()
  const logout = useLogoutIfAccessDenied()
  const [library, setLibrary] = useState('')
  const [search, setSearch] = useState('')
  const [query, setQuery] = useState('')
  const [page, setPage] = useState(1)
  const [data, setData] = useState<Awaited<ReturnType<typeof mediaClient.list>>>()
  const [candidates, setCandidates] = useState<Awaited<ReturnType<typeof mediaClient.candidates>>>()
  const [review, setReview] = useState<MediaLinkReview>()
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)
  const [reason, setReason] = useState('')
  const [confirmed, setConfirmed] = useState(false)
  const pending = useRef<AbortController | undefined>(undefined)
  useEffect(() => () => pending.current?.abort(), [])
  useEffect(() => {
    if (isPending) return
    const controller = new AbortController()
    const params = new URLSearchParams({ page: String(page) })
    if (library) params.set('library', library)
    if (query) params.set('q', query)
    void mediaClient.list(params, controller.signal).then(result => {
      if (!controller.signal.aborted) setData(result)
    }).catch(failure => { if (!controller.signal.aborted) {
      setError(failure instanceof Error ? failure.message : 'Media inventory failed.'); void logout(failure)
    } })
    return () => controller.abort()
  }, [library, query, page, isPending, logout])
  function resetView() {
    setData(undefined); setError(''); setCandidates(undefined); setReview(undefined)
  }
  async function action(task: (signal: AbortSignal) => Promise<void>) {
    if (pending.current) return
    const controller = new AbortController()
    pending.current = controller; setBusy(true); setError('')
    try { await task(controller.signal) }
    catch (failure) { if (!controller.signal.aborted) {
      setError(failure instanceof Error ? failure.message : 'Media operation failed.'); void logout(failure)
    } } finally { if (!controller.signal.aborted) { pending.current = undefined; setBusy(false) } }
  }
  return <Stack spacing={2} sx={{ p: 3, minWidth: 0, contain: 'inline-size', overflowWrap: 'anywhere' }}>
    <Title title="Media" /><Typography variant="h4" component="h1">Media inventory and Recording links</Typography>
    <Alert severity="info">Match using embedded tags and scoped sidecars, not filenames alone. No files are moved or retagged. Preparing and accepting a review reads/verifies audio bytes and may download cloud-only Dropbox files.</Alert>
    {error && <Alert severity="error">{error}</Alert>}
    {(busy || !data && !error) && <LinearProgress aria-label="Loading media" />}
    {busy && <Button onClick={() => {
      pending.current?.abort(); pending.current = undefined; setBusy(false)
      setError(review ? 'Request cancelled. Cancellation can race a completed server write; retry the same review to recover any saved outcome.'
        : 'Media request cancelled. No automatic retry; choose Review media to start again.')
    }}>Cancel media request</Button>}
    {data && <>
      {data.warnings.map((warning, index) => <Alert severity="warning" key={index}>{warning}</Alert>)}
      <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2}>
        <TextField select label="Media library" value={library} disabled={busy} onChange={event => {
          if (library !== event.target.value) { resetView(); setLibrary(event.target.value); setPage(1) }
        }}>
          <MenuItem value="">All libraries</MenuItem>{data.libraries.map(value => <MenuItem key={value} value={value}>{value}</MenuItem>)}
        </TextField>
        <TextField label="Search media tags or paths" value={search} disabled={busy} onChange={event => setSearch(event.target.value)} />
        <Button disabled={busy} onClick={() => { if (query !== search || page !== 1) { resetView(); setQuery(search); setPage(1) } }}>Search</Button>
      </Stack>
      <Typography>{data.total} historical scanner files. Page {page}.</Typography>
      <Box sx={{ width: '100%', overflowX: 'auto', contain: 'inline-size' }}>
        <Table size="small" aria-label="Media inventory"><TableHead><TableRow>
          <TableCell>Library / path</TableCell><TableCell>Embedded metadata</TableCell><TableCell>Last scanned</TableCell><TableCell>Review</TableCell>
        </TableRow></TableHead><TableBody>{data.items.map(item => <TableRow key={item.id}>
          <TableCell>{item.library}<Typography variant="body2">{item.relativePath}</Typography></TableCell>
          <TableCell>{item.title ?? 'No title'} {item.mix ?? ''}<Typography variant="body2">{item.artist ?? 'No Artist'}; {item.isrc ?? 'No ISRC'}; {item.durationSeconds ?? '?'} s</Typography></TableCell>
          <TableCell>{item.scannedUtc}</TableCell><TableCell><Button disabled={busy} onClick={() => void action(async signal => {
            setReview(undefined); setCandidates(undefined)
            const result = await mediaClient.candidates(item.id, signal)
            if (!signal.aborted) setCandidates(result)
          })}>Review media {item.id}</Button></TableCell>
        </TableRow>)}</TableBody></Table>
      </Box>
      <Stack direction="row" spacing={1}><Button disabled={busy || page === 1} onClick={() => { resetView(); setPage(page - 1) }}>Previous</Button>
        <Button disabled={busy || page * 25 >= data.total} onClick={() => { resetView(); setPage(page + 1) }}>Next</Button></Stack>
    </>}
    {candidates && <Stack component="section" aria-label="Media Recording candidates" spacing={2}>
      <Typography variant="h6" component="h2">{candidates.media.title ?? candidates.media.relativePath}</Typography>
      <Typography>{candidates.media.path}</Typography>
      {candidates.linkedRecordingId ? <Alert severity="success">Already linked to <Link to={`/recordings/${encodeURIComponent(candidates.linkedRecordingId)}/show`}>Recording</Link>. No replacement is offered.</Alert> : <>
        {!candidates.candidates.length && <Alert severity="info">No tagged metadata candidates. Refresh scanner inventory or admit the appropriate Catalog Recording first; no Recording is created here.</Alert>}
        {candidates.candidates.map(candidate => <Stack key={candidate.recordingId} spacing={1}>
          <Link to={`/recordings/${encodeURIComponent(candidate.recordingId)}/show`}>{candidate.title} {candidate.mix}</Link>
          <Typography>{candidate.status}. {candidate.evidence.join(' ')}</Typography>
          {candidate.blockers.map((blocker, index) => <Alert severity="warning" key={index}>{blocker}</Alert>)}
          <Button disabled={busy || candidate.blockers.length > 0} onClick={() => void action(async signal => {
            setReview(undefined); setReason(''); setConfirmed(false)
            const result = await mediaClient.prepare(candidates.media.id, candidate.recordingId, signal)
            if (!signal.aborted) setReview(result)
          })}>Verify file and prepare link to {candidate.recordingId}</Button>
        </Stack>)}
      </>}
    </Stack>}
    {review && <Stack component="section" aria-label="Frozen media link review" spacing={2}>
      <Typography component="h2" variant="h6">Verified media link review</Typography>
      <Typography>SHA-256: {review.verifiedSha256}</Typography>
      <Typography>Recording: {review.candidate.title} {review.candidate.mix}. {review.candidate.status}. {review.candidate.evidence.join(' ')}</Typography>
      <Typography>Sidecar: {review.sidecarTitle ?? 'Not available'} {review.sidecarMix ?? ''}. Review {review.id}, created {review.createdAt}; expires after 24 hours.</Typography>
      {review.outcome ? <Alert severity="success">Media link saved with permanent audit. <Link to={`/recordings/${encodeURIComponent(review.outcome.recordingId)}/show`}>View Recording and linked copies</Link></Alert> : <>
        <TextField label="Media link review reason" multiline value={reason} disabled={busy}
          slotProps={{ htmlInput: { maxLength: 2000 } }} onChange={event => setReason(event.target.value)} />
        <FormControlLabel label="I confirm this file is the same Recording/version, including any missing or uncertain metadata"
          control={<Checkbox disabled={busy} checked={confirmed} onChange={event => setConfirmed(event.target.checked)} />} />
        <Button variant="contained" disabled={busy || !reason.trim() || !confirmed} onClick={() => void action(async signal => {
          const outcome = await mediaClient.apply(review, reason, confirmed, signal)
          if (!signal.aborted) {
            setReview({ ...review, outcome })
            if (candidates) setCandidates({ ...candidates, linkedRecordingId: outcome.recordingId })
          }
        })}>Accept media Recording link</Button>
      </>}
    </Stack>}
  </Stack>
}

export function RecordingMedia({ id }: { id: string }) {
  const [files, setFiles] = useState<Awaited<ReturnType<typeof mediaClient.linked>>>()
  const [error, setError] = useState('')
  const logout = useLogoutIfAccessDenied()
  useEffect(() => {
    const controller = new AbortController()
    void mediaClient.linked(id, controller.signal).then(result => { if (!controller.signal.aborted) setFiles(result) })
      .catch(failure => { if (!controller.signal.aborted) {
        setError(failure instanceof Error ? failure.message : 'Linked media could not be loaded.'); void logout(failure)
      } })
    return () => controller.abort()
  }, [id, logout])
  return <Stack component="section" aria-label="Linked Recording media" spacing={1}>
    <Typography component="h2" variant="h6">Linked media copies</Typography>
    {error ? <Alert severity="warning">{error}</Alert> : files === undefined ? <LinearProgress /> : <>
      {!files.length && <Typography>No accepted media links. <Link to="/media">Review media inventory</Link>.</Typography>}
      {files.map(file => <Box key={file.id} sx={{ overflowWrap: 'anywhere' }}><Typography>{file.path}</Typography>
        <Typography variant="body2">SHA-256: {file.sha256 ?? 'Not recorded'}; linked {file.linkedAt ?? 'Unknown'}.</Typography></Box>)}
      {!!files.length && <Typography variant="body2">Persisted associations, not a fresh availability or complete NAS backup check. Playback and file operations are not enabled.</Typography>}
    </>}
  </Stack>
}
