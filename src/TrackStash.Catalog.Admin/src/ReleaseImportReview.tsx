import { useEffect, useRef, useState } from 'react'
import { Alert, Box, Button, Checkbox, FormControlLabel, LinearProgress, MenuItem, Stack, TextField, Typography } from '@mui/material'
import { useLogoutIfAccessDenied } from 'react-admin'
import { Link } from 'react-router-dom'
import { bulkImportReason, releaseImportClient, straightforwardImportDecisions } from './catalog'
import type { GroupPage, ImportDecision, ImportIdentity, ImportPreview, SourceTrack } from './catalog'

function decisionKey(item: { kind: string; providerId: number }) { return `${item.kind}:${item.providerId}` }
function validDecision(decision: ImportDecision) {
  return !!decision.evidence.trim() && (decision.action !== 'reuse' || !!decision.canonicalId?.trim())
}
function initialDecision(item: ImportIdentity): ImportDecision {
  const action = item.action === 'reuse' || item.action === 'unresolved' ? item.action : 'create'
  return { kind: item.kind, providerId: item.providerId, action,
    canonicalId: action === 'reuse' ? item.canonicalId : null, evidence: item.evidence ?? '' }
}

function Decision({ item, draft, busy, onChange, onSave }: {
  item: ImportIdentity; draft?: ImportDecision; busy: boolean
  onChange: (decision: ImportDecision) => void; onSave: (decision: ImportDecision) => void
}) {
  const decision = draft ?? initialDecision(item)
  const { action, canonicalId, evidence } = decision
  return <Box sx={{ border: '1px solid', borderColor: 'divider', p: 2, my: 1 }} component="section" aria-label={`Review ${item.kind} ${item.providerId}`}>
    <Typography component="h4" variant="h6">{item.kind}: {item.name} ({item.providerId})</Typography>
    <Typography>Current decision: {item.action}; canonical ID {item.canonicalId ?? 'not selected'}</Typography>
    {item.mixName && <Typography>Version: {item.mixName}; ISRC: {item.isrc ?? 'unknown'}</Typography>}
    {item.kind === 'recording' && <Typography>Reuse preserves existing Recording metadata and credits; source credits belong to the new placement.</Typography>}
    {item.compoundReview && <Alert severity="warning">This expression may be a collaboration or one genuine act. Do not split on punctuation. Explicitly accept the act or retain an unresolved compound for later evidence-backed splitting.</Alert>}
    {item.candidates.length > 0 && <><Typography>Similarity/provider candidates are not identity proof:</Typography>
      <ul>{item.candidates.map(c => <li key={c.id}>{c.name} - {c.mixName} - {c.isrc} - {c.id}</li>)}</ul></>}
    {item.action !== 'unchanged' && item.action !== 'blocked' && <Stack spacing={1} sx={{ mt: 1 }}>
      <TextField select label="Identity action" value={action} disabled={busy} onChange={e =>
        onChange({ ...decision, action: e.target.value as ImportDecision['action'], canonicalId: e.target.value === 'reuse' ? item.canonicalId : null })}>
        <MenuItem value="create">Accept a distinct new identity as supplied</MenuItem>
        {item.kind === 'artist' && <MenuItem value="unresolved">Retain an unresolved compound source identity</MenuItem>}
        {item.kind !== 'release' && <MenuItem value="reuse">Reuse an existing canonical identity with evidence</MenuItem>}
      </TextField>
      {action === 'reuse' && <TextField label="Canonical identity ID" value={canonicalId ?? ''} disabled={busy} onChange={e => onChange({ ...decision, canonicalId: e.target.value })}
        slotProps={{ htmlInput: { maxLength: 200 } }} helperText="Use an existing identity ID, not the provider ID. The server verifies ownership and version conflicts." />}
      <TextField label="Identity review evidence" multiline value={evidence} disabled={busy} onChange={e => onChange({ ...decision, evidence: e.target.value })}
        slotProps={{ htmlInput: { maxLength: 2000 } }} />
      <Button disabled={busy || !draft || !validDecision(decision)} onClick={() =>
        onSave({ ...decision, canonicalId: action === 'reuse' ? canonicalId!.trim() : null })}>
        Save decision in a new immutable preview
      </Button>
    </Stack>}
  </Box>
}

function FrozenPages({ preview, busy, drafts, onChange, onSave }: {
  preview: ImportPreview; busy: boolean; drafts: Record<string, ImportDecision>
  onChange: (decision: ImportDecision) => void; onSave: (decision: ImportDecision) => void
}) {
  const [page, setPage] = useState(1)
  const [trackPage, setTrackPage] = useState(1)
  const [loaded, setLoaded] = useState<{
    operationId: string; page: number; trackPage: number
    identities: GroupPage<ImportIdentity>; tracks: GroupPage<SourceTrack>
  }>()
  const current = loaded?.operationId === preview.operationId && loaded.page === page && loaded.trackPage === trackPage ? loaded : undefined
  const identities = current?.identities
  const tracks = current?.tracks
  const [error, setError] = useState('')
  const logout = useLogoutIfAccessDenied()
  useEffect(() => {
    const controller = new AbortController()
    void Promise.all([releaseImportClient.identities(preview.operationId, page, controller.signal),
      releaseImportClient.tracks(preview.operationId, trackPage, controller.signal)]).then(([identities, tracks]) => {
      if (!controller.signal.aborted) { setLoaded({ operationId: preview.operationId, page, trackPage, identities, tracks }); setError('') }
    }).catch(failure => {
      if (!controller.signal.aborted) { setError(failure instanceof Error ? failure.message : 'Frozen preview could not be read.'); void logout(failure) }
    })
    return () => controller.abort()
  }, [preview.operationId, page, trackPage, logout])
  if (error) return <Alert severity="error">{error}</Alert>
  return <>
    <Typography component="h3" variant="h6">Frozen identity review</Typography>
    {!identities ? <LinearProgress aria-label="Loading frozen identity page" /> : identities.items.map(item =>
      <Decision key={decisionKey(item)} item={item} draft={drafts[decisionKey(item)]} busy={busy} onChange={onChange} onSave={onSave} />)}
    <Stack direction="row" spacing={2} sx={{ my: 2 }}>
      <Button disabled={busy || page === 1} onClick={() => setPage(page - 1)}>Previous identities</Button>
      <Typography>Identity page {page}</Typography>
      <Button disabled={busy || page * 10 >= preview.identityCount} onClick={() => setPage(page + 1)}>Next identities</Button>
    </Stack>
    <Typography component="h3" variant="h6">Frozen release placements and source credits</Typography>
    {!tracks ? <LinearProgress aria-label="Loading frozen track page" /> : <ul>{tracks.items.map(track => <li key={track.sourcePosition}>
      Display/source position {track.sourcePosition} (official numbering unknown): {track.title} ({track.providerId}) - {track.mixName}
      <ul>{track.credits.map((credit, index) => <li key={index}>{credit.name} ({credit.providerId}) - {credit.role}, source position {credit.position}</li>)}</ul>
    </li>)}</ul>}
    <Stack direction="row" spacing={2} sx={{ my: 2 }}>
      <Button disabled={busy || trackPage === 1} onClick={() => setTrackPage(trackPage - 1)}>Previous frozen tracks</Button>
      <Typography>Frozen track page {trackPage}</Typography>
      <Button disabled={busy || trackPage * 10 >= preview.trackCount} onClick={() => setTrackPage(trackPage + 1)}>Next frozen tracks</Button>
    </Stack>
  </>
}

export function ReleaseImportReview({ releaseId, onApplied }: { releaseId: number; onApplied?: (catalogReleaseId: string) => void }) {
  const [preview, setPreview] = useState<ImportPreview>()
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [reason, setReason] = useState('')
  const [confirmed, setConfirmed] = useState(false)
  const [drafts, setDrafts] = useState<Record<string, ImportDecision>>({})
  const [reviewSession, setReviewSession] = useState(0)
  const [bulkMessage, setBulkMessage] = useState('')
  const mounted = useRef(true)
  const logout = useLogoutIfAccessDenied()
  useEffect(() => { mounted.current = true; return () => { mounted.current = false } }, [])
  async function create(decisions?: ImportDecision[], bulk = false) {
    setBusy(true); setError(''); setConfirmed(false)
    try {
      if (bulk && preview) {
        const identities: ImportIdentity[] = []
        for (let page = 1; identities.length < preview.identityCount; page++) {
          const result = await releaseImportClient.identities(preview.operationId, page)
          if (!mounted.current) return
          if (result.total !== preview.identityCount || result.items.length === 0)
            throw new Error('Incomplete frozen identity pages. No bulk decisions were accepted.')
          identities.push(...result.items)
        }
        const selected = straightforwardImportDecisions(identities, preview.warnings).filter(item => !drafts[decisionKey(item)])
        decisions = selected
        setBulkMessage(decisions.length
          ? `Bulk accepting ${decisions.length} straightforward new identities. Compound names, existing candidates, shared-recording warnings and your edited drafts are excluded.`
          : 'No straightforward undecided identities remain. Review the exceptions individually.')
        if (!decisions.length) return
        setReason(previous => previous.trim() ? previous : bulkImportReason)
        setDrafts(previous => ({ ...Object.fromEntries(selected.map(item => [decisionKey(item), item])), ...previous }))
      }
      if (decisions) {
        let current = preview
        for (const decision of decisions) {
          if (!current || !mounted.current) break
          const result = await releaseImportClient.preview(releaseId, current.operationId, decision)
          current = result
          if (mounted.current) {
            setPreview(result)
            setDrafts(previous => {
              const next = { ...previous }; delete next[decisionKey(decision)]; return next
            })
          }
        }
      } else {
        const result = await releaseImportClient.preview(releaseId)
        if (mounted.current) { setPreview(result); setReason(''); setDrafts({}); setBulkMessage(''); setReviewSession(previous => previous + 1) }
      }
    } catch (failure) {
      if (mounted.current) { setError(failure instanceof Error ? failure.message : 'Preview failed.'); void logout(failure) }
    } finally { if (mounted.current) setBusy(false) }
  }
  async function apply() {
    if (!preview) return
    setBusy(true); setError('')
    try {
      const outcome = await releaseImportClient.apply(preview, reason, confirmed)
      if (mounted.current) { setPreview({ ...preview, outcome }); onApplied?.(outcome.releaseId) }
    } catch (failure) {
      if (mounted.current) { setError(failure instanceof Error ? failure.message : 'Apply outcome unknown. Retry the same operation/reason to recover a saved result.'); void logout(failure) }
    } finally { if (mounted.current) setBusy(false) }
  }
  return <Box component="section" aria-label="Immutable catalog import review" sx={{ mt: 3 }}>
    <Typography component="h2" variant="h6">Reviewed catalog import</Typography>
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
      {!preview.outcome && <>
        <Alert severity="info">Edit multiple identities, then save the reviewed decisions together. Drafts and page positions are retained when saving or changing pages. Only starting a fresh preview discards them; final confirmation must be checked again after a save.</Alert>
        <Button disabled={busy || preview.noOp} onClick={() => void create(undefined, true)}>Bulk accept straightforward identities</Button>
        <Typography variant="body2">No typed reason needed: deliberate bulk acceptance is recorded in the audit. Compound names, existing candidates and potentially shared recordings still need individual review. No import is applied by this button.</Typography>
        {bulkMessage && <Alert severity="info">{bulkMessage}</Alert>}
        <Button disabled={busy || !Object.keys(drafts).length || !Object.values(drafts).every(validDecision)}
          onClick={() => void create(Object.values(drafts))}>Save all edited decisions ({Object.keys(drafts).length})</Button>
        <FrozenPages key={reviewSession} preview={preview} busy={busy} drafts={drafts}
          onChange={decision => { setDrafts(previous => ({ ...previous, [decisionKey(decision)]: decision })); setConfirmed(false) }}
          onSave={decision => void create([decision])} />
      </>}
      {preview.outcome ? <Alert severity="success">Saved {preview.outcome.noOp ? 'no-op' : 'import'} outcome for Release {preview.outcome.releaseId}.
        {' '}<Link to={`/releases/${encodeURIComponent(preview.outcome.releaseId)}/show`}>View release</Link>.
        {' '}{preview.outcome.createdArtists} Artists, {preview.outcome.createdRecordings} Recordings, {preview.outcome.createdPlacements} placements created. Audit {preview.outcome.auditId}.
        {' '}Embedding work remains pending; this does not assert purchases or media ownership.</Alert> : <Stack spacing={1} sx={{ my: 2 }}>
        <TextField label="Import review reason" multiline value={reason} disabled={busy} onChange={e => setReason(e.target.value)} slotProps={{ htmlInput: { maxLength: 2000 } }} />
        <FormControlLabel control={<Checkbox checked={confirmed} disabled={busy} onChange={e => setConfirmed(e.target.checked)} />}
          label="I reviewed the complete release and identity choices, and confirm this catalog operation" />
        <Button disabled={busy || !!preview.blockers.length || !!Object.keys(drafts).length || !reason.trim() || !confirmed} onClick={() => void apply()}>
          {preview.noOp ? 'Apply reviewed no-op' : 'Apply reviewed catalog import'}
        </Button>
      </Stack>}
    </>}
  </Box>
}
