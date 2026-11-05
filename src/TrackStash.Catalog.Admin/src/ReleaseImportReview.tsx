import { useEffect, useRef, useState } from 'react'
import { Alert, Box, Button, Checkbox, FormControlLabel, LinearProgress, MenuItem, Stack, TextField, Typography } from '@mui/material'
import { useLogoutIfAccessDenied } from 'react-admin'
import { Link } from 'react-router-dom'
import { releaseImportClient } from './catalog'
import type { GroupPage, ImportDecision, ImportIdentity, ImportPreview, SourceTrack } from './catalog'

function Decision({ item, busy, onSave }: { item: ImportIdentity; busy: boolean; onSave: (decision: ImportDecision) => void }) {
  const [action, setAction] = useState<ImportDecision['action']>('create')
  const [canonicalId, setCanonicalId] = useState('')
  const [evidence, setEvidence] = useState('')
  return <Box sx={{ border: '1px solid', borderColor: 'divider', p: 2, my: 1 }} component="section" aria-label={`Review ${item.kind} ${item.providerId}`}>
    <Typography component="h4" variant="h6">{item.kind}: {item.name} ({item.providerId})</Typography>
    <Typography>Current decision: {item.action}; canonical ID {item.canonicalId ?? 'not selected'}</Typography>
    {item.mixName && <Typography>Version: {item.mixName}; ISRC: {item.isrc ?? 'unknown'}</Typography>}
    {item.kind === 'recording' && <Typography>Reuse preserves existing Recording metadata and credits; source credits belong to the new placement.</Typography>}
    {item.compoundReview && <Alert severity="warning">This expression may be a collaboration or one genuine act. Do not split on punctuation. Explicitly accept the act or retain an unresolved compound for later evidence-backed splitting.</Alert>}
    {item.candidates.length > 0 && <><Typography>Similarity/provider candidates are not identity proof:</Typography>
      <ul>{item.candidates.map(c => <li key={c.id}>{c.name} - {c.mixName} - {c.isrc} - {c.id}</li>)}</ul></>}
    {item.action !== 'unchanged' && item.action !== 'blocked' && <Stack spacing={1} sx={{ mt: 1 }}>
      <TextField select label="Identity action" value={action} disabled={busy} onChange={e => setAction(e.target.value as ImportDecision['action'])}>
        <MenuItem value="create">Accept a distinct new identity as supplied</MenuItem>
        {item.kind === 'artist' && <MenuItem value="unresolved">Retain an unresolved compound source identity</MenuItem>}
        {item.kind !== 'release' && <MenuItem value="reuse">Reuse an existing canonical identity with evidence</MenuItem>}
      </TextField>
      {action === 'reuse' && <TextField label="Canonical identity ID" value={canonicalId} disabled={busy} onChange={e => setCanonicalId(e.target.value)}
        slotProps={{ htmlInput: { maxLength: 200 } }} helperText="Use an existing identity ID, not the provider ID. The server verifies ownership and version conflicts." />}
      <TextField label="Identity review evidence" multiline value={evidence} disabled={busy} onChange={e => setEvidence(e.target.value)}
        slotProps={{ htmlInput: { maxLength: 2000 } }} />
      <Button disabled={busy || !evidence.trim() || action === 'reuse' && !canonicalId.trim()} onClick={() =>
        onSave({ kind: item.kind, providerId: item.providerId, action, canonicalId: action === 'reuse' ? canonicalId.trim() : null, evidence })}>
        Save decision in a new immutable preview
      </Button>
    </Stack>}
  </Box>
}

function FrozenPages({ preview, busy, onSave }: { preview: ImportPreview; busy: boolean; onSave: (decision: ImportDecision) => void }) {
  const [page, setPage] = useState(1)
  const [trackPage, setTrackPage] = useState(1)
  const [identities, setIdentities] = useState<GroupPage<ImportIdentity>>()
  const [tracks, setTracks] = useState<GroupPage<SourceTrack>>()
  const [error, setError] = useState('')
  const logout = useLogoutIfAccessDenied()
  useEffect(() => {
    const controller = new AbortController()
    void Promise.all([releaseImportClient.identities(preview.operationId, page, controller.signal),
      releaseImportClient.tracks(preview.operationId, trackPage, controller.signal)]).then(([identities, tracks]) => {
      if (!controller.signal.aborted) { setIdentities(identities); setTracks(tracks); setError('') }
    }).catch(failure => {
      if (!controller.signal.aborted) { setError(failure instanceof Error ? failure.message : 'Frozen preview could not be read.'); void logout(failure) }
    })
    return () => controller.abort()
  }, [preview.operationId, page, trackPage, logout])
  if (error) return <Alert severity="error">{error}</Alert>
  return <>
    <Typography component="h3" variant="h6">Frozen identity review</Typography>
    {!identities ? <LinearProgress aria-label="Loading frozen identity page" /> : identities.items.map(item => <Decision key={`${item.kind}:${item.providerId}`} item={item} busy={busy} onSave={onSave} />)}
    <Stack direction="row" spacing={2} sx={{ my: 2 }}>
      <Button disabled={busy || page === 1} onClick={() => { setIdentities(undefined); setPage(page - 1) }}>Previous identities</Button>
      <Typography>Identity page {page}</Typography>
      <Button disabled={busy || page * 10 >= preview.identityCount} onClick={() => { setIdentities(undefined); setPage(page + 1) }}>Next identities</Button>
    </Stack>
    <Typography component="h3" variant="h6">Frozen release placements and source credits</Typography>
    {!tracks ? <LinearProgress aria-label="Loading frozen track page" /> : <ul>{tracks.items.map(track => <li key={track.sourcePosition}>
      Display/source position {track.sourcePosition} (official numbering unknown): {track.title} ({track.providerId}) - {track.mixName}
      <ul>{track.credits.map((credit, index) => <li key={index}>{credit.name} ({credit.providerId}) - {credit.role}, source position {credit.position}</li>)}</ul>
    </li>)}</ul>}
    <Stack direction="row" spacing={2} sx={{ my: 2 }}>
      <Button disabled={busy || trackPage === 1} onClick={() => { setTracks(undefined); setTrackPage(trackPage - 1) }}>Previous frozen tracks</Button>
      <Typography>Frozen track page {trackPage}</Typography>
      <Button disabled={busy || trackPage * 10 >= preview.trackCount} onClick={() => { setTracks(undefined); setTrackPage(trackPage + 1) }}>Next frozen tracks</Button>
    </Stack>
  </>
}

export function ReleaseImportReview({ releaseId }: { releaseId: number }) {
  const [preview, setPreview] = useState<ImportPreview>()
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [reason, setReason] = useState('')
  const [confirmed, setConfirmed] = useState(false)
  const mounted = useRef(true)
  const logout = useLogoutIfAccessDenied()
  useEffect(() => { mounted.current = true; return () => { mounted.current = false } }, [])
  async function create(decision?: ImportDecision) {
    setBusy(true); setError(''); setConfirmed(false)
    try {
      const result = await releaseImportClient.preview(releaseId, decision ? preview?.operationId : undefined, decision)
      if (mounted.current) { setPreview(result); setReason('') }
    } catch (failure) {
      if (mounted.current) { setError(failure instanceof Error ? failure.message : 'Preview failed.'); void logout(failure) }
    } finally { if (mounted.current) setBusy(false) }
  }
  async function apply() {
    if (!preview) return
    setBusy(true); setError('')
    try {
      const outcome = await releaseImportClient.apply(preview, reason, confirmed)
      if (mounted.current) setPreview({ ...preview, outcome })
    } catch (failure) {
      if (mounted.current) { setError(failure instanceof Error ? failure.message : 'Apply outcome unknown. Retry the same operation/reason to recover a saved result.'); void logout(failure) }
    } finally { if (mounted.current) setBusy(false) }
  }
  return <Box component="section" aria-label="Immutable catalog import review" sx={{ mt: 3 }}>
    <Typography component="h2" variant="h5">Reviewed catalog import</Typography>
    <Button disabled={busy} onClick={() => void create()}>{preview ? 'Start a fresh preview (discard decisions)' : 'Create immutable import preview'}</Button>
    {busy && <LinearProgress aria-label="Saving import review" />}
    {error && <Alert severity="error">{error} No automatic retry. If stale, start a fresh preview and review again.</Alert>}
    {preview && <>
      <Typography>Review {preview.operationId} expires {preview.expiresUtc}. Release: {preview.title} ({preview.sourceReleaseId})</Typography>
      {preview.labelId && <Link to={`/labels/${encodeURIComponent(preview.labelId)}/show`}>Canonical label {preview.labelId}</Link>}
      <Typography>Catalog number {preview.catalogNumber ?? 'unknown'}; UPC {preview.upc ?? 'unknown'}; publish date {preview.publishDate ?? 'unknown'}; new release date {preview.newReleaseDate ?? 'unknown'}</Typography>
      <Typography>{preview.createdArtists} new Artists; {preview.createdRecordings} new Recordings; {preview.trackCount} source placements. {preview.noOp ? 'No-op.' : 'Full release metadata, independent of files.'}</Typography>
      <Typography component="h3" variant="h6">Frozen release-level credits</Typography>
      <ul>{preview.releaseCredits.map((credit, index) => <li key={index}>{credit.name} ({credit.providerId}) - {credit.role}, source position {credit.position}</li>)}</ul>
      {preview.warnings.map((warning, index) => <Alert key={index} severity="info">{warning}</Alert>)}
      {preview.blockers.length > 0 && <Alert severity="warning"><Typography>Apply blocked:</Typography><ul>{preview.blockers.map((blocker, index) => <li key={index}>{blocker}</li>)}</ul></Alert>}
      {!preview.outcome && <FrozenPages key={preview.operationId} preview={preview} busy={busy} onSave={decision => void create(decision)} />}
      {preview.outcome ? <Alert severity="success">Saved {preview.outcome.noOp ? 'no-op' : 'import'} outcome for Release {preview.outcome.releaseId}.
        {' '}{preview.outcome.createdArtists} Artists, {preview.outcome.createdRecordings} Recordings, {preview.outcome.createdPlacements} placements created. Audit {preview.outcome.auditId}.
        {' '}Embedding work remains pending; this does not assert purchases or media ownership.</Alert> : <Stack spacing={1} sx={{ my: 2 }}>
        <TextField label="Import review reason" multiline value={reason} disabled={busy} onChange={e => setReason(e.target.value)} slotProps={{ htmlInput: { maxLength: 2000 } }} />
        <FormControlLabel control={<Checkbox checked={confirmed} disabled={busy} onChange={e => setConfirmed(e.target.checked)} />}
          label="I reviewed the complete release and identity choices, and confirm this catalog operation" />
        <Button disabled={busy || !!preview.blockers.length || !reason.trim() || !confirmed} onClick={() => void apply()}>
          {preview.noOp ? 'Apply reviewed no-op' : 'Apply reviewed catalog import'}
        </Button>
      </Stack>}
    </>}
  </Box>
}
