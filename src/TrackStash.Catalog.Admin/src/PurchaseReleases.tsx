import { useEffect, useState } from 'react'
import { Alert, Box, Button, LinearProgress, Stack, Table, TableBody, TableCell, TableHead, TableRow, TextField, Typography } from '@mui/material'
import { Title, useLogoutIfAccessDenied } from 'react-admin'
import { Link } from 'react-router-dom'
import { purchaseReleaseClient } from './catalog'
import type { GroupPage, PurchaseCandidate, PurchaseInspection, SourceCredit } from './catalog'
import { ReleaseImportReview } from './ReleaseImportReview'

function Credits({ items }: { items: SourceCredit[] }) {
  return items.length ? <ul>{items.map((credit, index) =>
    <li key={index}>{credit.name} ({credit.providerId}) - {credit.role}, source position {credit.position}</li>)}</ul> : <span>No credits in source</span>
}

function Inspection({ id, page, onPage }: { id: number; page: number; onPage: (page: number) => void }) {
  const [data, setData] = useState<PurchaseInspection>()
  const [error, setError] = useState('')
  const logout = useLogoutIfAccessDenied()
  useEffect(() => {
    const controller = new AbortController()
    void purchaseReleaseClient.inspect(id, page, controller.signal).then(result => {
      if (!controller.signal.aborted) setData(result)
    }).catch(failure => {
      if (!controller.signal.aborted) {
        setError(failure instanceof Error ? failure.message : 'Release inspection failed.')
        void logout(failure)
      }
    })
    return () => controller.abort()
  }, [id, page, logout])
  if (error) return <Alert severity="error">{error}</Alert>
  if (!data) return <LinearProgress aria-label="Loading release inspection" />
  const source = data.source
  return <Box component="section" aria-label="Release inspection" sx={{ mt: 3 }}>
    <Typography variant="h5" component="h2">{source.title ?? 'Unavailable release'} ({source.releaseId})</Typography>
    <Alert severity={source.sourceComplete ? 'info' : 'error'} sx={{ my: 1 }}>
      {source.sourceComplete ? 'Cached source coverage complete - identity review still required.' : 'Cached source incomplete or contradictory - cannot import.'}
      {' '}{source.hydratedTrackCount}/{source.manifestTrackCount} tracks hydrated; declared count {source.declaredTrackCount ?? 'unknown'}.
    </Alert>
    <Typography>Label: {source.label ? `${source.label.name} (${source.label.providerId})` : 'Unknown'}</Typography>
    <Typography>Canonical label matches: {data.labelCandidateCount}. {data.labelCandidateCount === 1 ? 'Candidate only; not an accepted binding.' : 'Review required.'}</Typography>
    {data.labelCandidates.map(label => <Box key={label.id}><Link to={`/labels/${encodeURIComponent(label.id)}/show`}>{label.name ?? label.id}</Link></Box>)}
    <Typography>Catalog number: {source.catalogNumber ?? 'Unknown'}; UPC: {source.upc ?? 'Unknown'}</Typography>
    <Typography>Source publish date: {source.publishDate ?? 'Unknown'}; source new release date: {source.newReleaseDate ?? 'Unknown'}</Typography>
    <Typography>Cache retrieved: {source.retrievedUtc ?? 'Unknown'} (not refreshed from Beatport)</Typography>
    <Typography variant="body2" sx={{ overflowWrap: 'anywhere' }}>Source bundle digest: {source.bundleDigest}</Typography>
    <Typography component="h3" variant="h6">Release credits</Typography>
    <Credits items={source.credits} />
    <Typography component="h3" variant="h6">Source diagnostics (whole release)</Typography>
    <ul>{source.diagnostics.map((diagnostic, index) => <li key={index}>
      {diagnostic.severity}: {diagnostic.code}{diagnostic.sourcePosition ? ` - source item ${diagnostic.sourcePosition}` : ''}: {diagnostic.message}
    </li>)}</ul>
    <Table aria-label="Source release tracks">
      <TableHead><TableRow><TableCell>Source position (not official numbering)</TableCell><TableCell>Track / version</TableCell><TableCell>Credits</TableCell><TableCell>Cache status</TableCell></TableRow></TableHead>
      <TableBody>{source.tracks.items.map(track => <TableRow key={track.sourcePosition}>
        <TableCell>{track.sourcePosition}</TableCell>
        <TableCell>{track.title ?? 'Missing details'} ({track.providerId ?? 'invalid ID'})<br />{track.mixName}
          <Typography variant="body2">ISRC: {track.isrc ?? 'Unknown'}; duration: {track.durationMs === null ? 'Unknown' : `${track.durationMs} ms`}</Typography>
        </TableCell>
        <TableCell><Credits items={track.credits} /></TableCell>
        <TableCell>{track.status}<br />{track.retrievedUtc ?? 'Unknown timestamp'}</TableCell>
      </TableRow>)}</TableBody>
    </Table>
    <Stack direction="row" spacing={2} sx={{ my: 2 }}>
      <Button disabled={page === 1} onClick={() => onPage(page - 1)}>Previous tracks</Button>
      <Typography>Track page {page}</Typography>
      <Button disabled={page * source.tracks.pageSize >= source.tracks.total} onClick={() => onPage(page + 1)}>Next tracks</Button>
    </Stack>
  </Box>
}

function Queue({ page, search, onSelect, onPage }: { page: number; search: string; onSelect: (id: number) => void; onPage: (page: number) => void }) {
  const [data, setData] = useState<GroupPage<PurchaseCandidate>>()
  const [error, setError] = useState('')
  const logout = useLogoutIfAccessDenied()
  useEffect(() => {
    const controller = new AbortController()
    void purchaseReleaseClient.list(page, search, controller.signal).then(result => {
      if (!controller.signal.aborted) setData(result)
    }).catch(failure => {
      if (!controller.signal.aborted) {
        setError(failure instanceof Error ? failure.message : 'Purchase candidates could not be loaded.')
        void logout(failure)
      }
    })
    return () => controller.abort()
  }, [page, search, logout])
  if (error) return <Alert severity="error">{error}</Alert>
  if (!data) return <LinearProgress aria-label="Loading purchase candidates" />
  return <>
    <Typography>{data.total} corpus-flagged release candidates</Typography>
    {!data.items.length && <Typography>No matching candidates.</Typography>}
    <Table aria-label="Purchase release candidates">
      <TableHead><TableRow><TableCell>Release</TableCell><TableCell>Source</TableCell><TableCell>Cache</TableCell><TableCell>Inspect</TableCell></TableRow></TableHead>
      <TableBody>{data.items.map(item => <TableRow key={item.releaseId}>
        <TableCell>{item.title} ({item.releaseId})</TableCell><TableCell>Derived corpus flag</TableCell>
        <TableCell>{item.hasCachedRelease ? 'Row present - completeness unverified' : 'Missing release payload'}</TableCell>
        <TableCell><Button onClick={() => onSelect(item.releaseId)} aria-label={`Inspect ${item.title} (${item.releaseId})`}>Inspect</Button></TableCell>
      </TableRow>)}</TableBody>
    </Table>
    <Stack direction="row" spacing={2} sx={{ my: 2 }}>
      <Button disabled={page === 1} onClick={() => onPage(page - 1)}>Previous candidates</Button>
      <Typography>Candidate page {page}</Typography>
      <Button disabled={page * data.pageSize >= data.total} onClick={() => onPage(page + 1)}>Next candidates</Button>
    </Stack>
  </>
}

export function PurchaseReleases() {
  const [search, setSearch] = useState('')
  const [query, setQuery] = useState('')
  const [page, setPage] = useState(1)
  const [selected, setSelected] = useState<number>()
  const [trackPage, setTrackPage] = useState(1)
  return <Box sx={{ p: 2 }}>
    <Title title="Purchase release inspector" />
    <Typography variant="h4" component="h1">Purchase release inspector</Typography>
    <Alert severity="info" sx={{ my: 2 }}>Inspection is read-only. Catalog import requires a separate immutable review and deliberate apply. No network refresh, purchase accounting or media changes. Corpus flags do not prove receipt or file ownership.</Alert>
    <Stack component="form" direction="row" spacing={2} sx={{ my: 2 }} onSubmit={event => {
      event.preventDefault(); setQuery(search); setPage(1); setSelected(undefined); setTrackPage(1)
    }}>
      <TextField label="Release title or Beatport ID" value={search} onChange={event => setSearch(event.target.value)} slotProps={{ htmlInput: { maxLength: 200 } }} />
      <Button type="submit">Search candidates</Button>
    </Stack>
    <Queue key={`${query}:${page}`} page={page} search={query} onPage={setPage} onSelect={id => { setSelected(id); setTrackPage(1) }} />
    {selected !== undefined && <Inspection key={`${selected}:${trackPage}`} id={selected} page={trackPage} onPage={setTrackPage} />}
    {selected !== undefined && <ReleaseImportReview key={selected} releaseId={selected} />}
  </Box>
}
