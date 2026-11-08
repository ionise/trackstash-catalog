import { useEffect, useRef, useState } from 'react'
import { Alert, Button, Checkbox, FormControlLabel, LinearProgress, Stack, TextField, Typography } from '@mui/material'
import { useLogoutIfAccessDenied } from 'react-admin'
import { Link } from 'react-router-dom'
import { mediaClient, mediaLibraryName } from './catalog'
import type { MediaGroup, MediaReleaseReview } from './catalog'

export function MediaReleaseLink({ group, libraryNames }: { group: MediaGroup; libraryNames: Record<string, string> }) {
  const logout = useLogoutIfAccessDenied()
  const [search, setSearch] = useState(group.release ?? group.name)
  const [results, setResults] = useState<Awaited<ReturnType<typeof mediaClient.releaseSearch>>>()
  const [review, setReview] = useState<MediaReleaseReview>()
  const [reason, setReason] = useState('')
  const [confirmed, setConfirmed] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const pending = useRef<AbortController | undefined>(undefined)
  useEffect(() => () => pending.current?.abort(), [])
  async function action(task: (signal: AbortSignal) => Promise<void>) {
    if (pending.current) return
    const controller = new AbortController()
    pending.current = controller
    setBusy(true); setError('')
    try { await task(controller.signal) } catch (failure) {
      if (!controller.signal.aborted) { setError(failure instanceof Error ? failure.message : 'Release association failed.'); void logout(failure) }
    } finally { if (!controller.signal.aborted) { pending.current = undefined; setBusy(false) } }
  }
  async function find(page: number) {
    await action(async signal => {
      const result = await mediaClient.releaseSearch(search, page, signal)
      if (!signal.aborted) setResults(result)
    })
  }
  return <Stack component="section" aria-label="Media Release association" spacing={1}>
    <Alert severity="info">Associate the entire embedded Label/Release group with a Catalog Release. No Recording links or placements are created, and audio is not verified.</Alert>
    {error && <Alert severity="error">{error}</Alert>}{busy && <LinearProgress />}
    <TextField label="Search Catalog Releases" value={search} disabled={busy} slotProps={{ htmlInput: { maxLength: 200 } }}
      onChange={event => { setSearch(event.target.value); setResults(undefined); setReview(undefined) }} />
    <Button disabled={busy} onClick={() => void find(1)}>Find Catalog Releases</Button>
    {results && <>
      {!results.items.length && <Typography>No Catalog Releases found. Change the search; no new Release is admitted here.</Typography>}
      {results.items.map(release => <Stack key={release.id} direction="row" spacing={1}>
        <Link to={`/releases/${encodeURIComponent(release.id)}/show`}>{release.title ?? release.id} / {release.labelName ?? 'Unknown Label'}</Link>
        <Button disabled={busy} onClick={() => void action(async signal => {
          setReview(undefined); setReason(''); setConfirmed(false)
          const result = await mediaClient.prepareRelease(group.key, release.id, signal)
          if (!signal.aborted) setReview(result)
        })}>Review association to {release.id}</Button>
      </Stack>)}
      <Stack direction="row"><Button disabled={busy || results.page <= 1} onClick={() => void find(results.page - 1)}>Previous Releases</Button>
        <Button disabled={busy || results.page * results.pageSize >= results.total} onClick={() => void find(results.page + 1)}>Next Releases</Button></Stack>
    </>}
    {review && <Stack component="section" aria-label="Frozen media Release review" spacing={1}>
      <Typography>{review.label} / {review.releaseTitle} → {review.catalogTitle}</Typography>
      <Typography>{review.files.length} files in the full group, including any excluded by list filters. Created {review.createdAt}; expires after 24 hours.</Typography>
      {review.evidence.map((value, index) => <Typography key={index}>{value}</Typography>)}
      {review.files.map(file => <Typography variant="body2" key={file.id}>{file.title} {file.mix} / {file.artist} / {mediaLibraryName(file.library, libraryNames)}</Typography>)}
      {review.outcome ? <Alert severity="success">Release association saved with permanent audit. <Link to={`/releases/${encodeURIComponent(review.releaseId)}/show?tab=Details`}>View associated Release</Link></Alert> : <>
        <TextField label="Release association reason" value={reason} disabled={busy} multiline
          slotProps={{ htmlInput: { maxLength: 2000 } }} onChange={event => setReason(event.target.value)} />
        <FormControlLabel label="I confirm the entire media group belongs to this Release, including any metadata differences"
          control={<Checkbox checked={confirmed} disabled={busy} onChange={event => setConfirmed(event.target.checked)} />} />
        <Button disabled={busy || !confirmed || !reason.trim()} variant="contained" onClick={() => void action(async signal => {
          const outcome = await mediaClient.applyRelease(review, reason, confirmed, signal)
          if (!signal.aborted) setReview({ ...review, outcome })
        })}>Accept Release association</Button>
      </>}
    </Stack>}
  </Stack>
}

export function ReleaseMediaGroups({ id }: { id: string }) {
  const logout = useLogoutIfAccessDenied()
  const [items, setItems] = useState<Awaited<ReturnType<typeof mediaClient.releaseGroups>>>()
  const [error, setError] = useState('')
  const [names, setNames] = useState<Record<string, string>>({})
  useEffect(() => {
    const controller = new AbortController()
    void Promise.all([mediaClient.releaseGroups(id, controller.signal), mediaClient.libraries(controller.signal)]).then(([result, settings]) => {
      if (!controller.signal.aborted) { setItems(result); setNames(Object.fromEntries(settings.libraries.map(l => [l.id, l.name]))) }
    })
      .catch(failure => { if (!controller.signal.aborted) {
        setError(failure instanceof Error ? failure.message : 'Media groups failed to load.'); void logout(failure)
      } })
    return () => controller.abort()
  }, [id, logout])
  return <Stack component="section" aria-label="Associated media release groups" spacing={1}>
    <Typography variant="h6" component="h2">Associated media groups</Typography>
    {error ? <Alert severity="warning">{error}</Alert> : !items ? <LinearProgress /> : <>
      {!items.length && <Typography>No reviewed media release groups. <Link to="/media">Browse media</Link>.</Typography>}
      {items.map(item => <Typography key={item.reviewId}>{item.label} / {item.mediaReleaseTitle}: {item.fileCount} files.
        Libraries: {item.libraries.map(l => mediaLibraryName(l, names)).join(', ')}. Associated {item.appliedAt}. Review {item.reviewId}.</Typography>)}
      <Typography variant="body2">Reviewed metadata associations, not individual Recording matches or verified backup coverage.</Typography>
    </>}
  </Stack>
}
