import { Fragment, useEffect, useRef, useState } from 'react'
import { Alert, Box, Button, Chip, Collapse, FormControlLabel, IconButton, LinearProgress, Stack, Switch, Table, TableBody, TableCell, TableHead, TableRow, TextField, Tooltip, Typography } from '@mui/material'
import CheckCircleIcon from '@mui/icons-material/CheckCircle'
import ExpandLess from '@mui/icons-material/ExpandLess'
import ErrorOutline from '@mui/icons-material/Error'
import ExpandMore from '@mui/icons-material/ExpandMore'
import WarningAmber from '@mui/icons-material/WarningAmber'
import { Title, useLogoutIfAccessDenied } from 'react-admin'
import { Link } from 'react-router-dom'
import { purchaseReleaseClient } from './catalog'
import type { PurchaseCandidate, PurchaseCandidatePage, PurchaseInspection, SourceCredit } from './catalog'
import { ReleaseImportReview } from './ReleaseImportReview'
import { BatchAdmission } from './BatchAdmission'

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
  return <Box component="section" aria-label="Release inspection" sx={{ mt: 1 }}>
    <Typography variant="h6" component="h2">{source.title ?? 'Unavailable release'} ({source.releaseId})</Typography>
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

const hideAdmittedKey = 'trackstash.purchaseReleases.hideAdmitted'
const readyOnlyKey = 'trackstash.purchaseReleases.readyOnly'

function ReadinessStatus({ item }: { item: PurchaseCandidate }) {
  const r = item.readiness
  if (!r) return <Tooltip title="The source release could not be summarised. Inspect it for details." describeChild>
    <Chip size="small" variant="outlined" color="error" icon={<ErrorOutline />} label="Unavailable" />
  </Tooltip>
  if (r.readiness === 'ready') return <Tooltip title="Cached source is complete and the label matches exactly one catalog label. Identity review is still required." describeChild>
    <Chip size="small" color="success" icon={<CheckCircleIcon />} label="Ready" />
  </Tooltip>
  if (r.readiness === 'incomplete_source') return <Tooltip describeChild
    title={`Cached source cannot be imported${r.blockerCodes.length ? `: ${r.blockerCodes.join(', ').replaceAll('_', ' ')}` : ''}.`}>
    <Chip size="small" color="warning" variant="outlined" icon={<WarningAmber />}
      label={r.manifestTrackCount ? `Incomplete source - ${r.hydratedTrackCount} of ${r.manifestTrackCount} tracks cached` : 'Incomplete source'} />
  </Tooltip>
  return <Tooltip describeChild title={r.labelCandidateCount ? `The source label matches ${r.labelCandidateCount} catalog labels.` : 'The source label matches no catalog label.'}>
    <Chip size="small" color="warning" variant="outlined" icon={<WarningAmber />} label="Label needs review" />
  </Tooltip>
}

function AdmissionStatus({ item, available, localReleaseId }: { item: PurchaseCandidate; available: boolean; localReleaseId?: string }) {
  const ids = localReleaseId && !item.admittedReleaseIds.includes(localReleaseId) ? [...item.admittedReleaseIds, localReleaseId] : item.admittedReleaseIds
  if (ids.length === 1) return <Tooltip title="Open the catalog Release that owns this Beatport release ID" describeChild>
    <Chip component={Link} to={`/releases/${encodeURIComponent(ids[0])}/show`} clickable size="small" color="success"
      icon={<CheckCircleIcon />} label="Admitted" aria-label={`Admitted - view catalog release for ${item.title}`} />
  </Tooltip>
  if (ids.length > 1) return <Tooltip title="More than one catalog Release owns this Beatport release ID. Resolve identity before importing." describeChild>
    <Chip size="small" color="warning" icon={<WarningAmber />} label={`Admitted ${ids.length}×`} />
  </Tooltip>
  if (!available) return <Tooltip title="Admission status needs catalog schema 9 or later" describeChild>
    <Chip size="small" variant="outlined" label="Unknown" />
  </Tooltip>
  return <Chip size="small" variant="outlined" label="Not admitted" />
}

function CandidateDetails({ item, onApplied, onCollapse }: { item: PurchaseCandidate; onApplied: (releaseId: string) => void; onCollapse: () => void }) {
  const [trackPage, setTrackPage] = useState(1)
  return <Box id={`purchase-candidate-${item.releaseId}`} sx={{ py: 1, minWidth: 0, contain: 'inline-size' }}>
    <Box sx={{ overflowX: 'auto' }}>
      <Inspection key={`${item.releaseId}:${trackPage}`} id={item.releaseId} page={trackPage} onPage={setTrackPage} />
    </Box>
    <ReleaseImportReview key={item.releaseId} releaseId={item.releaseId} onApplied={onApplied} />
    <Button startIcon={<ExpandLess />} onClick={onCollapse} sx={{ mt: 1 }}>Collapse {item.title}</Button>
  </Box>
}

function Queue({ page, search, hideAdmitted, readyOnly, onPage }: { page: number; search: string; hideAdmitted: boolean; readyOnly: boolean; onPage: (page: number) => void }) {
  const [data, setData] = useState<PurchaseCandidatePage>()
  const [error, setError] = useState('')
  const [expanded, setExpanded] = useState<number>()
  const [applied, setApplied] = useState<Record<number, string>>({})
  const [reload, setReload] = useState(0)
  const reloadedFor = useRef(new Set<number>())
  const rows = useRef(new Map<number, HTMLTableRowElement>())
  const logout = useLogoutIfAccessDenied()
  useEffect(() => {
    const controller = new AbortController()
    void purchaseReleaseClient.list(page, search, controller.signal, hideAdmitted, readyOnly).then(result => {
      if (!controller.signal.aborted) setData(result)
    }).catch(failure => {
      if (!controller.signal.aborted) {
        setError(failure instanceof Error ? failure.message : 'Purchase candidates could not be loaded.')
        void logout(failure)
      }
    })
    return () => controller.abort()
  }, [page, search, hideAdmitted, readyOnly, logout, reload])
  // With admitted releases hidden, drop a just-imported row once its details are closed so the result stays visible until then.
  useEffect(() => {
    if (!hideAdmitted || !data) return
    const stale = data.items.filter(item => applied[item.releaseId] && item.releaseId !== expanded && !reloadedFor.current.has(item.releaseId))
    if (!stale.length) return
    stale.forEach(item => reloadedFor.current.add(item.releaseId))
    setReload(previous => previous + 1)
  }, [hideAdmitted, data, applied, expanded])
  useEffect(() => {
    if (data && !data.items.length && page > 1) onPage(page - 1)
  }, [data, page, onPage])
  useEffect(() => {
    if (expanded !== undefined) rows.current.get(expanded)?.scrollIntoView?.({ block: 'start', behavior: 'smooth' })
  }, [expanded])
  if (error) return <Alert severity="error">{error}</Alert>
  if (!data) return <LinearProgress aria-label="Loading purchase candidates" />
  const toggle = (id: number) => setExpanded(current => current === id ? undefined : id)
  return <>
    <Typography>{data.total} corpus-flagged release candidates{data.readyOnly ? ' ready to import' : ''}{data.hideAdmitted ? ' not yet admitted' : ''}</Typography>
    {!data.admissionStatusAvailable && <Alert severity="warning" sx={{ my: 1 }}>Admission status needs catalog schema 9 or later; every candidate is shown as Unknown.</Alert>}
    {!data.items.length && <Typography>No matching candidates.</Typography>}
    <Box sx={{ minWidth: 0, contain: 'inline-size', overflowX: 'auto' }}>
      <Table aria-label="Purchase release candidates" size="small">
        <TableHead><TableRow>
          <TableCell padding="checkbox">Details</TableCell>
          <TableCell>Release</TableCell><TableCell>Ready</TableCell><TableCell>Catalog</TableCell>
        </TableRow></TableHead>
        <TableBody>{data.items.map(item => {
          const open = expanded === item.releaseId
          const label = `${item.title} (${item.releaseId})`
          return <Fragment key={item.releaseId}>
            <TableRow hover selected={open} ref={element => { if (element) rows.current.set(item.releaseId, element); else rows.current.delete(item.releaseId) }}
              sx={{ cursor: 'pointer', '& > td': open ? { borderBottom: 'unset' } : undefined, scrollMarginTop: 72 }}
              onClick={event => { if (!(event.target as HTMLElement).closest('a,button')) toggle(item.releaseId) }}>
              <TableCell padding="checkbox">
                <Tooltip title={open ? 'Collapse release details' : 'Inspect release and review import'}>
                  <IconButton size="small" aria-label={`Inspect ${label}`} aria-expanded={open}
                    aria-controls={open ? `purchase-candidate-${item.releaseId}` : undefined} onClick={() => toggle(item.releaseId)}>
                    {open ? <ExpandLess /> : <ExpandMore />}
                  </IconButton>
                </Tooltip>
              </TableCell>
              <TableCell sx={{ overflowWrap: 'anywhere' }}>{item.title}<Typography variant="body2" color="text.secondary">Beatport {item.releaseId}</Typography></TableCell>
              <TableCell><ReadinessStatus item={item} /></TableCell>
              <TableCell><AdmissionStatus item={item} available={data.admissionStatusAvailable} localReleaseId={applied[item.releaseId]} /></TableCell>
            </TableRow>
            {open && <TableRow>
              <TableCell colSpan={4} sx={{ py: 0, bgcolor: 'action.hover', maxWidth: 0 }}>
                <Collapse in appear timeout="auto">
                  <CandidateDetails item={item} onCollapse={() => { setExpanded(undefined); rows.current.get(item.releaseId)?.querySelector('button')?.focus() }}
                    onApplied={releaseId => setApplied(current => ({ ...current, [item.releaseId]: releaseId }))} />
                </Collapse>
              </TableCell>
            </TableRow>}
          </Fragment>
        })}</TableBody>
      </Table>
    </Box>
    <Stack direction="row" spacing={2} useFlexGap sx={{ my: 2, flexWrap: 'wrap', alignItems: 'center' }}>
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
  const [hideAdmitted, setHideAdmitted] = useState(() => localStorage.getItem(hideAdmittedKey) === 'true')
  const [readyOnly, setReadyOnly] = useState(() => localStorage.getItem(readyOnlyKey) === 'true')
  const [generation, setGeneration] = useState(0)
  return <Box sx={{ p: 2, minWidth: 0 }}>
    <Title title="Purchase release inspector" />
    <Typography variant="h4" component="h1">Purchase release inspector</Typography>
    <Alert severity="info" sx={{ my: 2 }}>Inspection is read-only. Catalog import requires a separate immutable review and deliberate apply. No network refresh, purchase accounting or media changes. Corpus flags do not prove receipt or file ownership.</Alert>
    <BatchAdmission onAdmitted={() => setGeneration(value => value + 1)} />
    <Stack component="form" direction="row" spacing={2} useFlexGap sx={{ my: 2, flexWrap: 'wrap', alignItems: 'center' }} onSubmit={event => {
      event.preventDefault(); setQuery(search); setPage(1)
    }}>
      <TextField label="Release title or Beatport ID" value={search} onChange={event => setSearch(event.target.value)} slotProps={{ htmlInput: { maxLength: 200 } }} />
      <Button type="submit">Search candidates</Button>
      <FormControlLabel label="Hide admitted releases" control={<Switch checked={hideAdmitted} onChange={event => {
        setHideAdmitted(event.target.checked); setPage(1); localStorage.setItem(hideAdmittedKey, String(event.target.checked))
      }} />} />
      <FormControlLabel label="Ready to import only" control={<Switch checked={readyOnly} onChange={event => {
        setReadyOnly(event.target.checked); setPage(1); localStorage.setItem(readyOnlyKey, String(event.target.checked))
      }} />} />
    </Stack>
    <Queue key={`${query}:${page}:${hideAdmitted}:${readyOnly}:${generation}`} page={page} search={query} hideAdmitted={hideAdmitted} readyOnly={readyOnly} onPage={setPage} />
  </Box>
}
