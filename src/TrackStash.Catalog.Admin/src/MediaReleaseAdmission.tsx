import { useEffect, useRef, useState } from 'react'
import { Alert, Box, Button, Dialog, DialogActions, DialogContent, DialogTitle, LinearProgress, Stack, Table, TableBody, TableCell, TableHead, TableRow, TextField, Typography } from '@mui/material'
import { useLogoutIfAccessDenied } from 'react-admin'
import { Link } from 'react-router-dom'
import { beatportClient } from './catalog'
import type { BeatportReleaseDetail, BeatportReleaseItem, BeatportReleasePage, MediaInventoryItem } from './catalog'
import { ReleaseImportReview } from './ReleaseImportReview'
import { MediaMatches } from './MediaMatches'

export function MediaReleaseAdmission({ items, libraryNames, onClose, onSaved }: {
  items: MediaInventoryItem[]; libraryNames: Record<string, string>; onClose: () => void; onSaved: () => void
}) {
  const media = items[0]
  const [query, setQuery] = useState((media.release ?? media.album ?? media.title ?? '').slice(0, 200))
  const [results, setResults] = useState<BeatportReleasePage>()
  const [detail, setDetail] = useState<BeatportReleaseDetail>()
  const [chosen, setChosen] = useState<BeatportReleaseItem>()
  const [catalogRelease, setCatalogRelease] = useState('')
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)
  const [importBusy, setImportBusy] = useState(false)
  const [linkBusy, setLinkBusy] = useState(false)
  const pending = useRef<AbortController | undefined>(undefined)
  const logout = useLogoutIfAccessDenied()
  const locked = busy || importBusy || linkBusy
  useEffect(() => () => pending.current?.abort(), [])
  async function action(task: (signal: AbortSignal) => Promise<void>) {
    if (pending.current) return
    const controller = new AbortController()
    pending.current = controller; setBusy(true); setError('')
    try { await task(controller.signal) }
    catch (failure) {
      if (!controller.signal.aborted) {
        setError(failure instanceof Error ? failure.message : 'Beatport Release lookup failed.'); void logout(failure)
      }
    } finally { if (pending.current === controller) { pending.current = undefined; setBusy(false) } }
  }
  async function search(page: number, signal: AbortSignal) {
    const found = await beatportClient.releases({ q: query.trim(), page, pageSize: 10, sort: 'name' }, signal)
    if (!signal.aborted) setResults(found)
  }
  return <Dialog open fullWidth maxWidth="lg" aria-labelledby="media-admission-title" onClose={() => { if (!locked) onClose() }}>
    <DialogTitle id="media-admission-title">Find Release, admit and link media</DialogTitle>
    <DialogContent dividers sx={{ minWidth: 0, overflowWrap: 'anywhere' }}>
      <Stack spacing={2}>
        <Typography>{media.title ?? 'Untitled'} {media.mix ?? ''} — {media.artist ?? 'Unknown Artist'}; {items.length} media copies carried into this workflow.</Typography>
        <Alert severity="info">1. Find the Beatport Release. 2. Review and admit it using the existing importer. 3. Tick and accept media matches here.
          Admission and linking are separate saves: if verification fails, the admitted Release stays in the Catalog and linking can be retried. No typed reason is required.</Alert>
        {error && <Alert severity="error">{error} If Beatport sign-in is required, use <Link to="/purchase-releases">Purchase releases</Link> to connect, then retry.</Alert>}
        {busy && <LinearProgress aria-label="Looking up Release for media admission" />}
        {!catalogRelease && <>
          <Stack direction={{ xs: 'column', sm: 'row' }} spacing={1}>
            <TextField fullWidth label="Beatport Release name" value={query} disabled={locked}
              slotProps={{ htmlInput: { maxLength: 200 } }} onChange={event => {
                setQuery(event.target.value); setResults(undefined)
              }} helperText="Seeded from embedded Release/album tags. Adjust the name if needed; no file or audio is uploaded." />
            <Button disabled={locked || !query.trim()} onClick={() => void action(async signal => {
              setChosen(undefined); setDetail(undefined); await search(1, signal)
            })}>Find Beatport Release</Button>
          </Stack>
          {results && <Box sx={{ overflowX: 'auto' }}>
            <Table size="small" aria-label="Beatport Releases for media">
              <TableHead><TableRow><TableCell>Release / Artists</TableCell><TableCell>Label / Catalog number</TableCell><TableCell>Choose</TableCell></TableRow></TableHead>
              <TableBody>{results.items.map(item => <TableRow key={item.id}>
                <TableCell>{item.name}<Typography variant="body2">{item.artists.map(a => a.name).join(', ')}</Typography></TableCell>
                <TableCell>{item.label?.name ?? 'Unknown Label'}<Typography variant="body2">{item.catalogNumber ?? ''}</Typography></TableCell>
                <TableCell><Button disabled={locked} onClick={() => void action(async signal => {
                  setChosen(undefined); setDetail(undefined)
                  const loaded = await beatportClient.release(item.id, signal)
                  if (signal.aborted) return
                  setDetail(loaded)
                  if (loaded.release.catalogReleaseIds.length === 1) {
                    setChosen(item); setCatalogRelease(loaded.release.catalogReleaseIds[0]); return
                  }
                  if (loaded.release.catalogReleaseIds.length > 1)
                    throw new Error('Several Catalog Releases reference this Beatport identity. Resolve the duplicate identities before admission.')
                  const source = await beatportClient.prepareImport(item.id, signal)
                  if (signal.aborted) return
                  if (!source.sourceCompleteAfter)
                    throw new Error(`Beatport metadata remains incomplete: ${source.remainingBlockers.join(', ')}. Retry fetching or resolve the source before admission.`)
                  setChosen(item)
                })}>Review Release {item.name}</Button></TableCell>
              </TableRow>)}</TableBody>
            </Table>
            {!results.items.length && <Typography>No Beatport Releases found. Adjust the Release name; the media remains unmatched.</Typography>}
            <Stack direction="row" spacing={1}>
              <Button disabled={locked || results.page <= 1} onClick={() => void action(signal => search(results.page - 1, signal))}>Previous Release results</Button>
              <Button disabled={locked || results.page * results.pageSize >= results.total} onClick={() => void action(signal => search(results.page + 1, signal))}>Next Release results</Button>
            </Stack>
          </Box>}
          {detail && <Box component="section" aria-label="Chosen Beatport Release tracks">
            <Typography variant="h6">{detail.release.name}</Typography>
            {detail.tracks.map(track => <Typography key={track.id}>{track.name} {track.mixName} — {track.artists.map(a => a.name).join(', ')}; {track.isrc ?? 'No ISRC'}</Typography>)}
          </Box>}
          {chosen && <Box>
            <Alert severity="info">The importer retains identity review and existing Label requirements. It can admit a cached Beatport Release without marking it as purchased.
              If its Label needs attention, use <Link to="/labels">Label maintenance</Link>; no Label is silently invented.</Alert>
            <ReleaseImportReview key={chosen.id} releaseId={chosen.id} disabled={busy} onBusyChange={setImportBusy}
              onApplied={id => { setImportBusy(false); setCatalogRelease(id); onSaved() }} />
          </Box>}
        </>}
        {catalogRelease && <>
          <Alert severity="success">Release is in the Catalog. <Link to={`/releases/${encodeURIComponent(catalogRelease)}/show`}>View Release</Link>.
            {' '}Now review the media proposals below. If linking fails, this Release is retained; retry the media batch, not admission.</Alert>
          <MediaMatches items={items} libraryNames={libraryNames} onBusy={setLinkBusy} onSaved={onSaved} />
        </>}
      </Stack>
    </DialogContent>
    <DialogActions><Button disabled={locked} onClick={onClose}>Close media admission</Button></DialogActions>
  </Dialog>
}
