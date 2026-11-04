import { useEffect, useState } from 'react'
import { Alert, Box, Button, Checkbox, Dialog, DialogActions, DialogContent, DialogTitle, FormControlLabel, Stack, TextField, Typography } from '@mui/material'
import { useDataProvider, useLogoutIfAccessDenied, useRecordContext, useRedirect, useRefresh } from 'react-admin'
import { mergeClient } from './catalog'
import type { LabelRecord, MergePreview } from './catalog'
import { ArtworkImage } from './LabelArtwork'

export function LabelMerge() {
  const record = useRecordContext<LabelRecord>()
  const provider = useDataProvider()
  const redirect = useRedirect()
  const refresh = useRefresh()
  const logoutIfAccessDenied = useLogoutIfAccessDenied()
  const [open, setOpen] = useState(false)
  const [search, setSearch] = useState('')
  const [source, setSource] = useState('')
  const [externalId, setExternalId] = useState('')
  const [candidates, setCandidates] = useState<LabelRecord[]>([])
  const [page, setPage] = useState(1)
  const [total, setTotal] = useState(0)
  const [preview, setPreview] = useState<MergePreview>()
  const [reason, setReason] = useState('')
  const [operator, setOperator] = useState('')
  const [confirmed, setConfirmed] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [expired, setExpired] = useState(false)
  useEffect(() => {
    const timer = preview ? setTimeout(() => setExpired(true), Math.max(0, Date.parse(preview.expiresUtc) - Date.now())) : undefined
    return () => clearTimeout(timer)
  }, [preview])
  if (!record) return null

  async function execute(action: () => Promise<void>) {
    setBusy(true)
    setError('')
    try { await action() }
    catch (failure) {
      setError(failure instanceof Error ? failure.message : 'Merge request failed.')
      await logoutIfAccessDenied(failure)
    } finally { setBusy(false) }
  }

  function find(nextPage: number) {
    void execute(async () => {
      const result = await provider.getList<LabelRecord>('labels', {
        pagination: { page: nextPage, perPage: 10 }, sort: { field: 'name', order: 'ASC' },
        filter: { q: search, source, externalId },
      })
      setCandidates(result.data)
      setTotal(result.total ?? 0)
      setPage(nextPage)
    })
  }

  function review(survivorId: string, retiredId: string) {
    void execute(async () => {
      setPreview(undefined)
      setExpired(false)
      setConfirmed(false)
      setPreview(await mergeClient.preview(survivorId, retiredId))
    })
  }

  function apply() {
    if (!preview) return
    void execute(async () => {
      const outcome = await mergeClient.apply(preview, reason, operator)
      setOpen(false)
      refresh()
      redirect('show', 'labels', outcome.canonicalId)
    })
  }

  return <>
    <Button onClick={() => { setOpen(true); setPreview(undefined); setCandidates([]); setError(''); setReason(''); setOperator(''); setConfirmed(false); setTotal(0) }}>
      Merge duplicate into this label
    </Button>
    <Dialog open={open} onClose={() => { if (!busy) setOpen(false) }} fullWidth maxWidth="md">
      <DialogTitle>Review duplicate label merge</DialogTitle>
      <DialogContent>
        <Stack spacing={2} sx={{ pt: 1 }}>
          {error && <Alert severity="error">{error}</Alert>}
          {!preview ? <>
            <Typography>Survivor: {record.name} ({record.id})</Typography>
            <Typography>Select another label only if it represents the same real label, not a parent or imprint.</Typography>
            <Box component="form" onSubmit={event => { event.preventDefault(); find(1) }}>
              <TextField label="Find duplicate label" value={search} onChange={event => setSearch(event.target.value)} disabled={busy} fullWidth />
              <TextField label="Provider (optional, e.g. beatport)" value={source} onChange={event => setSource(event.target.value)} disabled={busy} fullWidth />
              <TextField label="Provider ID (optional)" value={externalId} onChange={event => setExternalId(event.target.value)} disabled={busy} fullWidth />
              <Button type="submit" disabled={busy}>Search candidates</Button>
            </Box>
            {candidates.filter(candidate => candidate.id !== record.id).map(candidate =>
              <Button key={candidate.id} disabled={busy} onClick={() => review(record.id, candidate.id)}>
                Review {candidate.name} ({candidate.id})
              </Button>)}
            <Stack direction="row" spacing={2}>
              <Button disabled={busy || page <= 1} onClick={() => find(page - 1)}>Previous candidates</Button>
              <Typography>Page {page}; {total} matching labels</Typography>
              <Button disabled={busy || page * 10 >= total} onClick={() => find(page + 1)}>Next candidates</Button>
            </Stack>
          </> : <>
            <Typography>Keep: {preview.survivor.name} ({preview.survivor.id})</Typography>
            <Typography>Retire: {preview.retired.name} ({preview.retired.id})</Typography>
            <Typography>Kept sort name: {preview.survivor.sortName ?? '(none)'}</Typography>
            {!!preview.artwork?.length && <>
              <Typography variant="h6">Resulting artwork (survivor first)</Typography>
              {preview.artwork.map((item, index) => <Box key={item.id}>
                <ArtworkImage assetId={item.assetId} caption={item.caption || `Merged artwork ${index + 1}`} />
                <Typography>{index + 1}. {item.caption || item.role}{item.isPreferred ? ' (preferred)' : ''}</Typography>
              </Box>)}
            </>}
            {preview.profile && <>
              <Typography variant="h6">Resulting label information</Typography>
              <Typography sx={{ whiteSpace: 'pre-wrap' }}>Description: {preview.profile.description ?? '(none)'}</Typography>
              <Typography>Location: {preview.profile.countryCode ?? '(none)'} / {preview.profile.city ?? '(none)'}</Typography>
              <Typography>Founded: {preview.profile.foundedYear ?? '(unknown)'}; status: {preview.profile.lifecycleStatus}</Typography>
              <Typography sx={{ whiteSpace: 'pre-wrap' }}>Personal notes: {preview.profile.notes ?? '(none)'}</Typography>
              <Typography>Favourite: {preview.profile.favourite ? 'Yes' : 'No'}; review: {preview.profile.reviewStatus}</Typography>
              {preview.profile.links.map(link => <Typography key={link.url}>{link.kind}: {link.url}</Typography>)}
            </>}
            <Typography>Review expires: {new Date(preview.expiresUtc).toLocaleString()}</Typography>
            <Button disabled={busy} onClick={() => review(preview.retired.id, preview.survivor.id)}>Switch survivor and review again</Button>
            <Typography variant="h6">Resulting aliases</Typography>
            {preview.aliases.map(alias => <Typography key={alias.value}>{alias.value}{alias.isPrimary ? ' (primary)' : ''}</Typography>)}
            <Typography variant="h6">Retained provider IDs</Typography>
            {preview.externalReferences.map(reference =>
              <Typography key={`${reference.source}/${reference.externalId}`}>{reference.source}: {reference.externalId}{reference.isPrimary ? ' (preferred)' : ''}</Typography>)}
            <Typography variant="h6">Resulting release links ({preview.releaseLinks.length})</Typography>
            {preview.releaseLinks.map(link => <Typography key={link.releaseId}>{link.releaseId}: {link.role ?? '(no role)'}{link.isPrimary ? ' (primary)' : ''}</Typography>)}
            {preview.warnings.map(warning => <Alert key={warning} severity="warning">{warning}</Alert>)}
            {preview.blockers.map(blocker => <Alert key={blocker} severity="error">{blocker}</Alert>)}
            <TextField label="Reason for merge" value={reason} onChange={event => setReason(event.target.value)} disabled={busy} required multiline slotProps={{ htmlInput: { maxLength: 2000 } }} />
            <TextField label="Operator (optional, unverified)" value={operator} onChange={event => setOperator(event.target.value)} disabled={busy} slotProps={{ htmlInput: { maxLength: 200 } }} />
            <FormControlLabel control={<Checkbox checked={confirmed} onChange={event => setConfirmed(event.target.checked)} disabled={busy} />}
              label={`These are the same label. Keep ${preview.survivor.id} and permanently retire ${preview.retired.id}.`} />
          </>}
        </Stack>
      </DialogContent>
      <DialogActions>
        <Button disabled={busy} onClick={() => setOpen(false)}>Cancel</Button>
        {preview && <Button disabled={busy} onClick={() => review(preview.survivor.id, preview.retired.id)}>Refresh preview</Button>}
        {preview && <Button variant="contained" disabled={busy || !confirmed || !reason.trim() || preview.blockers.length > 0 || expired} onClick={apply}>
          Confirm merge
        </Button>}
      </DialogActions>
    </Dialog>
  </>
}
