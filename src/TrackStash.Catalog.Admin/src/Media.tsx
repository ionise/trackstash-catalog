import { useEffect, useRef, useState } from 'react'
import { Accordion, AccordionDetails, AccordionSummary, Alert, Box, Button, Checkbox, Chip, FormControlLabel, LinearProgress, MenuItem, Stack, Table, TableBody, TableCell, TableHead, TableRow, TextField, Typography } from '@mui/material'
import { Title, useAuthenticated, useLogoutIfAccessDenied } from 'react-admin'
import { Link } from 'react-router-dom'
import { mediaClient, mediaLibraryName as libraryName } from './catalog'
import type { MediaGroup, MediaLinkReview } from './catalog'
import { MediaReleaseLink } from './MediaReleaseLink'
import { MediaReconciliation } from './MediaReconciliation'

export function MediaInventory() {
  const { isPending } = useAuthenticated()
  const logout = useLogoutIfAccessDenied()
  const [library, setLibrary] = useState('')
  const [search, setSearch] = useState('')
  const [query, setQuery] = useState('')
  const [page, setPage] = useState(1)
  const [groupBy, setGroupBy] = useState('release')
  const [group, setGroup] = useState<MediaGroup>()
  const [sort, setSort] = useState('release')
  const [direction, setDirection] = useState('asc')
  const [filters, setFilters] = useState({ label: '', release: '', artist: '', track: '' })
  const [appliedFilters, setAppliedFilters] = useState(filters)
  const [data, setData] = useState<Awaited<ReturnType<typeof mediaClient.list>>>()
  const [candidates, setCandidates] = useState<Awaited<ReturnType<typeof mediaClient.candidates>>>()
  const [review, setReview] = useState<MediaLinkReview>()
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)
  const [reason, setReason] = useState('')
  const [confirmed, setConfirmed] = useState(false)
  const [revision, setRevision] = useState(0)
  const [individualFiles, setIndividualFiles] = useState(false)
  const pending = useRef<AbortController | undefined>(undefined)
  const names = data?.libraryNames ?? {}
  const name = (id: string) => libraryName(id, names)
  useEffect(() => () => pending.current?.abort(), [])
  useEffect(() => {
    if (isPending) return
    const controller = new AbortController()
    const params = new URLSearchParams({ page: String(page), groupBy, sort, direction })
    if (group) params.set('groupKey', group.key)
    for (const [name, value] of Object.entries(appliedFilters)) if (value) params.set(name, value)
    if (library) params.set('library', library)
    if (query) params.set('q', query)
    void mediaClient.list(params, controller.signal).then(result => {
      if (!controller.signal.aborted) setData(result)
    }).catch(failure => { if (!controller.signal.aborted) {
      setError(failure instanceof Error ? failure.message : 'Media inventory failed.'); void logout(failure)
    } })
    return () => controller.abort()
  }, [library, query, page, groupBy, group, sort, direction, appliedFilters, isPending, logout, revision])
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
    <Title title="Media" /><Typography variant="h4" component="h1">{group?.canAssociateRelease ? 'Release reconciliation' : 'Media inventory and Recording links'}</Typography>
    <Link to="/media-libraries">Manage libraries</Link>
    {!group?.canAssociateRelease && <Alert severity="info">Match using embedded tags and scoped sidecars, not filenames alone. No files are moved or retagged. Preparing and accepting a review reads/verifies audio bytes and may download cloud-only Dropbox files.</Alert>}
    {error && <Alert severity="error">{error}</Alert>}
    {(busy || !data && !error) && <LinearProgress aria-label="Loading media" />}
    {busy && <Button onClick={() => {
      pending.current?.abort(); pending.current = undefined; setBusy(false)
      setError(review ? 'Request cancelled. Cancellation can race a completed server write; retry the same review to recover any saved outcome.'
        : 'Media request cancelled. No automatic retry; choose Review media to start again.')
    }}>Cancel media request</Button>}
    {data && <>
      {!group?.canAssociateRelease && <>
      {data.warnings.map((warning, index) => <Alert severity="warning" key={index}>{warning}</Alert>)}
      <Stack direction={{ xs: 'column', sm: 'row' }} spacing={2}>
        <TextField select label="Media library" value={library} disabled={busy} onChange={event => {
          if (library !== event.target.value) { resetView(); setLibrary(event.target.value); setPage(1) }
        }}>
          <MenuItem value="">All libraries</MenuItem>{data.libraries.map(value => <MenuItem key={value} value={value}>{name(value)}</MenuItem>)}
        </TextField>
        <TextField label="Search embedded metadata" value={search} disabled={busy} slotProps={{ htmlInput: { maxLength: 200 } }}
          onChange={event => setSearch(event.target.value)} />
        <Button disabled={busy} onClick={() => {
          resetView(); setQuery(search); setAppliedFilters({ ...filters }); setPage(1)
        }}>Search</Button>
      </Stack>
      <Box sx={{ display: 'flex', gap: 2, flexWrap: 'wrap' }}>
        {(['label', 'release', 'artist', 'track'] as const).map(name => <TextField key={name} label={`Filter ${name}`}
          value={filters[name]} disabled={busy} slotProps={{ htmlInput: { maxLength: 200 } }}
          onChange={event => setFilters({ ...filters, [name]: event.target.value })} />)}
        <TextField select label="Group media by" value={groupBy} disabled={busy} onChange={event => {
          if (groupBy !== event.target.value) { resetView(); setGroup(undefined); setGroupBy(event.target.value); setPage(1) }
        }}>{[['none', 'Individual files'], ['label', 'Label'], ['release', 'Release'], ['artist', 'Artist'],
          ['track', 'Track'], ['fingerprint', 'Audio fingerprint']].map(([value, name]) => <MenuItem key={value} value={value}>{name}</MenuItem>)}</TextField>
        <TextField select label="Sort media by" value={sort} disabled={busy} onChange={event => {
          if (sort !== event.target.value) { resetView(); setSort(event.target.value); setPage(1) }
        }}>{['title', 'label', 'release', 'artist', 'library'].map(value => <MenuItem key={value} value={value}>{value}</MenuItem>)}</TextField>
        <TextField select label="Sort direction" value={direction} disabled={busy} onChange={event => {
          if (direction !== event.target.value) { resetView(); setDirection(event.target.value); setPage(1) }
        }}><MenuItem value="asc">Ascending</MenuItem><MenuItem value="desc">Descending</MenuItem></TextField>
      </Box>
      <Typography>{data.total} historical scanner files{!group && groupBy !== 'none' ? ` in ${data.groupTotal ?? 0} groups` : ''}. Page {page}.</Typography>
      </>}
      {group && <>
        <Button disabled={busy} onClick={() => { resetView(); setGroup(undefined); setPage(1) }}>Back to groups</Button>
        <Typography variant="h6">{group.name}</Typography>
        {group.associatedReleaseId && <Alert severity="success">Associated with <Link to={`/releases/${encodeURIComponent(group.associatedReleaseId)}/show?tab=Details`}>{group.associatedReleaseTitle ?? group.associatedReleaseId}</Link>.</Alert>}
        {group.canAssociateRelease && <MediaReconciliation key={group.key} group={group} libraryNames={names} onSaved={outcome => {
          setCandidates(undefined); setReview(undefined)
          setGroup({ ...group, associatedReleaseId: outcome.releaseId, associatedReleaseTitle: outcome.association.releaseTitle })
          setRevision(value => value + 1)
        }} />}
        {!group.associatedReleaseId && group.canAssociateRelease && <Accordion>
          <AccordionSummary>Release association only (without track links)</AccordionSummary>
          <AccordionDetails><MediaReleaseLink key={group.key} group={group} libraryNames={names} /></AccordionDetails>
        </Accordion>}
        {group.canAssociateRelease && !!data.warnings.length && <Accordion>
          <AccordionSummary>Scanner inventory notes and limitations</AccordionSummary>
          <AccordionDetails>{data.warnings.map((warning, index) => <Alert severity="warning" key={index}>{warning}</Alert>)}</AccordionDetails>
        </Accordion>}
      </>}
      {groupBy === 'fingerprint' && <Alert severity="info">Exact stored acoustic fingerprints only, not fuzzy matching or byte-identical backup proof. Files without fingerprints stay separate.</Alert>}
      {group?.canAssociateRelease && <FormControlLabel label="Show individual file reviews (advanced)"
        control={<Checkbox checked={individualFiles} onChange={event => setIndividualFiles(event.target.checked)} />} />}
      {!group && <Alert severity="info">To assign media with fewer clicks, group by Release and choose Organise Release. Other groupings remain useful for exploring labels, Artists and shared audio.</Alert>}
      <Box sx={{ width: '100%', overflowX: 'auto', contain: 'inline-size' }}>
        {!!data.groups?.length && <Table size="small" aria-label="Media groups"><TableHead><TableRow>
          <TableCell>Metadata group</TableCell><TableCell>Libraries</TableCell><TableCell>Files</TableCell><TableCell>Details</TableCell>
        </TableRow></TableHead><TableBody>{data.groups.map(item => <TableRow key={item.key}>
          <TableCell>{item.name}<Typography variant="body2">{item.label ?? 'Unknown Label'}{groupBy !== 'release' && item.release ? ` / ${item.release}` : ''}</Typography>
            {item.associatedReleaseId && <Link to={`/releases/${encodeURIComponent(item.associatedReleaseId)}/show?tab=Details`}>Associated: {item.associatedReleaseTitle}</Link>}</TableCell>
          <TableCell>{item.libraries.map(name).join(', ')}</TableCell><TableCell>{item.fileCount}</TableCell>
          <TableCell><Button disabled={busy} onClick={() => { resetView(); setGroup(item); setPage(1); setIndividualFiles(false) }}>{item.canAssociateRelease ? 'Organise Release' : 'Open group'} {item.name}</Button></TableCell>
        </TableRow>)}</TableBody></Table>}
        {(!group?.canAssociateRelease || individualFiles) && (group || groupBy === 'none' || !!data.items.length) &&
        <Table size="small" aria-label="Media inventory"><TableHead><TableRow>
          <TableCell>Library</TableCell><TableCell>Track / Artist</TableCell><TableCell>Label / Release</TableCell><TableCell>Review</TableCell>
        </TableRow></TableHead><TableBody>{data.items.map(item => <TableRow key={item.id}>
          <TableCell>{name(item.library)}</TableCell>
          <TableCell>{item.title ?? 'No title'} {item.mix ?? ''}<Typography variant="body2">{item.artist ?? 'No Artist'}; {item.isrc ?? 'No ISRC'}; {item.durationSeconds ?? '?'} s</Typography></TableCell>
          <TableCell>{item.label ?? 'Unknown Label'}<Typography variant="body2">{item.release ?? item.album ?? 'Unknown Release'}</Typography></TableCell><TableCell><Button disabled={busy} onClick={() => void action(async signal => {
            setReview(undefined); setCandidates(undefined)
            const result = await mediaClient.candidates(item.id, signal)
            if (!signal.aborted) setCandidates(result)
          })}>Review media {item.id}</Button></TableCell>
        </TableRow>)}</TableBody></Table>}
      </Box>
      {!data.items.length && !data.groups?.length && <Typography>No media matches these filters.</Typography>}
      {(!group?.canAssociateRelease || individualFiles) && <Stack direction="row" spacing={1}><Button disabled={busy || page === 1} onClick={() => { resetView(); setPage(page - 1) }}>Previous</Button>
        <Button disabled={busy || page * 25 >= (!group && groupBy !== 'none' ? data.groupTotal ?? 0 : data.total)} onClick={() => { resetView(); setPage(page + 1) }}>Next</Button></Stack>}
    </>}
    {candidates && <Stack component="section" aria-label="Media Recording candidates" spacing={2}>
      <Typography variant="h6" component="h2">{candidates.media.title ?? candidates.media.relativePath}</Typography>
      <Typography>{candidates.media.path}</Typography>
      <Typography variant="body2">Last scanned: {candidates.media.scannedUtc}; Library: {name(candidates.media.library)}</Typography>
      {!candidates.media.contentHash?.trim() && <Alert severity="info">No scanner SHA-256 is stored. Verify file will calculate this copy's byte hash and freeze it for acceptance without changing the scanner. Matching audio copies can have different hashes because of tags.</Alert>}
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
  const fingerprintKeys = [...new Set(files?.map(f => f.fingerprintKey).filter((key): key is string => !!key) ?? [])]
  const fingerprintCount = files?.filter(f => f.fingerprintKey).length ?? 0
  return <Stack component="section" aria-label="Linked Recording media" spacing={1}>
    <Typography component="h2" variant="h6">Linked media copies</Typography>
    {error ? <Alert severity="warning">{error}</Alert> : files === undefined ? <LinearProgress /> : <>
      {!files.length && <Typography>No accepted media links. <Link to="/media">Review media inventory</Link>.</Typography>}
      {!!files.length && <>
        <Alert severity={fingerprintCount > 1 && fingerprintKeys.length === 1 ? 'success' : 'info'}>
          {fingerprintCount > 1 && fingerprintKeys.length === 1
            ? `${fingerprintCount} copies share an exact stored audio fingerprint.`
            : fingerprintKeys.length > 1
              ? `${fingerprintKeys.length} different stored fingerprint groups. Different fingerprints do not necessarily mean different audio.`
              : fingerprintCount === 1 ? 'Only one copy has stored audio fingerprint evidence; cross-copy comparison is unavailable.'
                : 'No stored audio fingerprints are available for these links.'}
          {fingerprintCount < files.length && ` ${files.length - fingerprintCount} copies have no stored fingerprint.`}
        </Alert>
        <Typography variant="body2">Audio evidence comes from the scanner snapshot retained when each copy was linked, not a fresh audio check or AcoustID lookup. Exact matches use the same fingerprint algorithm/version. AIFF and FLAC can represent the same lossless audio while their file hashes differ; fingerprints alone do not prove identical decoded samples.</Typography>
      </>}
      {files.map(file => {
        const location = file.libraryName ?? file.library ?? 'Unconfigured location'
        const count = file.fingerprintKey ? files.filter(f => f.fingerprintKey === file.fingerprintKey).length : 0
        return <Stack key={file.id} spacing={1} sx={{ border: 1, borderColor: 'divider', p: 2, minWidth: 0, overflowWrap: 'anywhere' }}>
          <Stack direction="row" spacing={1} sx={{ flexWrap: 'wrap', gap: 1 }}>
            <Typography sx={{ fontWeight: 'bold' }}>{location}</Typography>
            <Chip size="small" label={file.format || 'Format unknown'} />
            <Chip size="small" color={count > 1 ? 'success' : 'default'} label={file.fingerprintKey
              ? `Audio group ${fingerprintKeys.indexOf(file.fingerprintKey) + 1}${count > 1 ? ` · matches ${count - 1} other ${count === 2 ? 'copy' : 'copies'}` : ' · no matching copy'}`
              : 'Audio fingerprint not recorded'} />
          </Stack>
          <Accordion disableGutters>
            <AccordionSummary>Location and file details — {location} {file.format}</AccordionSummary>
            <AccordionDetails sx={{ overflowWrap: 'anywhere' }}>
              <Typography>{file.path}</Typography>
              <Typography variant="body2">SHA-256 (whole-file bytes): {file.sha256 ?? 'Not recorded'}</Typography>
              <Typography variant="body2">Linked {file.linkedAt ?? 'Unknown'}. Media identity: {file.id}.</Typography>
              <Typography variant="body2">SHA-256 includes tags, artwork and container encoding. Different hashes between copies are expected and do not indicate an audio mismatch.</Typography>
            </AccordionDetails>
          </Accordion>
        </Stack>
      })}
      {!!files.length && <Typography variant="body2">Reviewed links, not a fresh availability or complete backup check. Playback and file operations are not enabled.</Typography>}
    </>}
  </Stack>
}
