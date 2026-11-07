import { useEffect, useRef, useState } from 'react'
import { Alert, Box, Button, Checkbox, FormControlLabel, LinearProgress, MenuItem, Stack, Table, TableBody, TableCell, TableHead, TableRow, TextField, Typography } from '@mui/material'
import { useLogoutIfAccessDenied } from 'react-admin'
import { enrichmentClient } from './catalog'
import type { EnrichmentLookup, EnrichmentReview } from './catalog'

export function RecordingEnrichment({ id, onApplied }: { id: string; onApplied?: () => void }) {
  return <EntityEnrichment key={id} id={id} resource="recordings" onApplied={onApplied} />
}

export function NamedEntityEnrichment({ id, resource, onApplied }: { id: string; resource: 'artists' | 'labels' | 'releases'; onApplied?: () => void }) {
  return <EntityEnrichment key={`${resource}:${id}`} id={id} resource={resource} onApplied={onApplied} />
}

function EntityEnrichment({ id, resource, onApplied }: { id: string; resource: 'recordings' | 'artists' | 'labels' | 'releases'; onApplied?: () => void }) {
  const entityName = resource === 'recordings' ? 'Recording' : resource === 'artists' ? 'Artist' : resource === 'releases' ? 'Release' : 'Label'
  const [provider, setProvider] = useState<'sonovault' | 'beatport'>('sonovault')
  const providerName = provider === 'beatport' ? 'Beatport' : 'SonoVault'
  const [result, setResult] = useState<EnrichmentLookup>()
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)
  const [review, setReview] = useState<EnrichmentReview>()
  const pending = useRef<AbortController | undefined>(undefined)
  const logout = useLogoutIfAccessDenied()
  useEffect(() => () => pending.current?.abort(), [id])

  async function lookup(candidateId?: string, editionId?: string) {
    if (pending.current) return
    const controller = new AbortController()
    pending.current = controller
    setBusy(true); setError('')
    setReview(undefined)
    if (candidateId === undefined) setResult(undefined)
    try {
      const data = candidateId === undefined
        ? await enrichmentClient.lookup(resource, id, controller.signal, provider)
        : await enrichmentClient.releaseCandidate(id, candidateId, editionId, controller.signal, provider)
      if (!controller.signal.aborted) setResult(data)
    } catch (failure) {
      if (!controller.signal.aborted) {
        setError(failure instanceof Error ? failure.message : `${providerName} lookup failed.`)
        void logout(failure)
      }
    } finally {
      if (!controller.signal.aborted) { setBusy(false); pending.current = undefined }
    }
  }

  async function prepare(candidateId: string, editionId: string | null) {
    if (pending.current || !result || (resource !== 'recordings' && resource !== 'releases')) return
    const controller = new AbortController()
    pending.current = controller; setBusy(true); setError(''); setReview(undefined)
    try {
      const data = await enrichmentClient.prepare(resource, id, candidateId, editionId, result.query.catalogVersion, controller.signal, provider)
      if (!controller.signal.aborted) setReview(data)
    } catch (failure) {
      if (!controller.signal.aborted) { setError(failure instanceof Error ? failure.message : 'Review preparation failed.'); void logout(failure) }
    } finally {
      if (!controller.signal.aborted) { pending.current = undefined; setBusy(false) }
    }
  }
  async function apply(fields: string[], preferred: string | null, reason: string, confirmed: boolean) {
    if (pending.current || !review) return
    const controller = new AbortController()
    pending.current = controller; setBusy(true); setError('')
    try {
      const outcome = await enrichmentClient.apply(review, fields, preferred, reason, confirmed, controller.signal)
      if (!controller.signal.aborted) { setReview({ ...review, outcome }); onApplied?.() }
    } catch (failure) {
      if (!controller.signal.aborted) { setError(failure instanceof Error ? failure.message : 'Enrichment apply failed.'); void logout(failure) }
    } finally {
      if (!controller.signal.aborted) { pending.current = undefined; setBusy(false) }
    }
  }

  return <Stack component="section" aria-label={`${providerName} enrichment`} spacing={2} sx={{ minWidth: 0, overflowWrap: 'anywhere' }}>
    <Typography component="h2" variant="h6">Metadata enrichment</Typography>
    {(resource === 'releases' || resource === 'recordings') && <TextField select label="Enrichment provider" value={provider} disabled={busy} onChange={event => {
      if (event.target.value !== 'beatport' && event.target.value !== 'sonovault') return
      setProvider(event.target.value); setResult(undefined); setError(''); setReview(undefined)
    }} sx={{ maxWidth: 300 }}>
      <MenuItem value="sonovault">SonoVault</MenuItem><MenuItem value="beatport">Beatport</MenuItem>
    </TextField>}
    <Alert severity="info">{(resource === 'recordings' || resource === 'releases')
      ? `Lookups do not change this ${entityName}. Prepare a review to select fields and explicitly confirm an update. Preparing a review fetches the chosen candidate once more and freezes its values; apply uses no provider requests.`
      : `Read-only pilot: compare ${providerName} candidates with this ${entityName}. Nothing is saved or applied.`} {provider === 'sonovault' ? 'Each lookup/review click may use one API request; there are no automatic retries.' : resource === 'recordings'
        ? 'Beatport uses one caching-broker call per Recording lookup/review. Duration is evidence only; no automatic retries.'
        : 'Beatport uses the caching broker: one search call or two detail/review calls (Release and tracks). Artwork is attached separately using the artwork manager.'}</Alert>
    <Button variant="outlined" disabled={busy} onClick={() => void lookup()} sx={{ alignSelf: 'flex-start' }}>Find enrichment on {providerName}</Button>
    {busy && <LinearProgress aria-label={`Looking up ${providerName} metadata`} />}
    {error && <Alert severity="error">{error}</Alert>}
    {result && <>
      <Typography variant="body2">Lookup {result.retrievedAt}; catalog snapshot {result.query.catalogVersion}. Comparison is against the snapshot used for this lookup.</Typography>
      {result.warnings.map((warning, index) => <Alert key={index} severity="warning">{warning}</Alert>)}
      {!result.candidates.length && <Alert severity="info">No {providerName} candidates found. The lookup succeeded; no catalog changes were made.</Alert>}
      {result.candidates.map(candidate => {
        const metadata = candidate.metadata
        const rows: { field: string; label: string; current: string | number | null; proposed: string | number | null }[] = resource === 'recordings' ? [
          { field: 'Name', label: 'Title', current: result.query.name, proposed: metadata.name },
          { field: 'MixName', label: 'Mix / version', current: result.query.mixName, proposed: metadata.mixName },
          { field: 'Isrc', label: 'ISRC', current: result.query.isrc, proposed: metadata.isrc },
          { field: 'DurationMs', label: 'Duration (ms)', current: result.query.durationMs, proposed: metadata.durationMs },
          { field: 'Bpm', label: 'BPM', current: result.query.catalogMusicalMetadata?.bpm ?? null, proposed: metadata.bpm },
          { field: 'MusicalKey', label: 'Musical key', current: result.query.catalogMusicalMetadata?.keyValue ? `${result.query.catalogMusicalMetadata.keyValue} (${result.query.catalogMusicalMetadata.keyNotation})` : null, proposed: metadata.musicalKey ? `${metadata.musicalKey.value} (${metadata.musicalKey.notation})` : null },
          { field: 'Genre', label: 'Genre', current: result.query.catalogMusicalMetadata?.genres?.join(', ') || null, proposed: metadata.genres?.join(', ') || metadata.genre },
          { field: 'Subgenre', label: 'Subgenre', current: result.query.catalogMusicalMetadata?.subgenres?.join(', ') || null, proposed: metadata.subgenres?.join(', ') || null },
          { field: 'Credits', label: 'Artist credits', current: `Primary Recording artists: ${result.query.artistNames.join(', ') || 'Not recorded'}`, proposed: metadata.credits?.map(c => `${c.name} (${c.role})`).join(', ') || null },
          { field: 'ExternalReferences', label: 'Identifiers', current: 'See Recording provider identities', proposed: metadata.externalReferences?.map(r => `${r.source}: ${r.externalId}`).join(', ') || null },
        ] : [
          { field: 'Name', label: resource === 'releases' ? 'Title' : 'Name', current: result.query.name, proposed: metadata.name },
          ...(resource === 'releases' ? [
            { field: 'Credits', label: 'Primary Release artists', current: result.query.artistNames.join(', ') || 'Not recorded',
              proposed: metadata.credits?.map(c => `${c.name} (${c.role})`).join(', ') || null },
            { field: 'LabelName', label: 'Label', current: result.query.labelNames?.join(', ') || result.query.labelName || 'Not recorded', proposed: metadata.labelName },
            { field: 'CatalogNumber', label: provider === 'beatport' ? 'Beatport catalog number' : 'Selected edition catalog number', current: result.query.catalogNumber ?? null, proposed: metadata.catalogNumber },
            { field: 'ReleaseDetails', label: provider === 'beatport' ? 'Provider release date' : 'Provider group date (not an edition date)', current: result.query.catalogPublishDate ?? null,
              proposed: metadata.releaseDetails?.reportedReleaseDate ?? null },
          ] : []),
          ...(resource === 'artists' ? [
            { field: 'Country', label: 'Country of origin', current: 'Not included in this identity snapshot', proposed: metadata.country ?? null },
            { field: 'FormationYear', label: 'Start / birth year', current: 'Not included in this identity snapshot', proposed: metadata.formationYear ?? null },
            { field: 'FormationDate', label: 'Start / birth date', current: 'Not included in this identity snapshot', proposed: metadata.formationDate ?? null },
            { field: 'SocialLinks', label: 'Social handles / URLs', current: 'Not included in this identity snapshot',
              proposed: Object.entries(metadata.socialLinks ?? {}).map(([source, value]) => `${source}: ${value}`).join(', ') || null },
          ] : []),
          { field: 'ExternalReferences', label: 'Identifiers',
            current: result.query.externalReferences?.map(r => `${r.source}: ${r.externalId}`).join(', ') || 'Not recorded',
            proposed: metadata.externalReferences?.map(r => `${r.source}: ${r.externalId}`).join(', ') || null },
        ]
        return <Stack key={`${candidate.providerId}:${candidate.candidateId}:${candidate.editionId ?? ''}`} spacing={1}>
          <Typography component="h3" variant="h6">{providerName} candidate {candidate.candidateId}{candidate.editionId ? ` / edition ${candidate.editionId}` : ''}</Typography>
          <Typography>Match basis: {candidate.match.basis}. {candidate.match.explanation}</Typography>
          {candidate.match.conflicts.map((conflict, index) => <Alert severity="warning" key={index}>{conflict}</Alert>)}
          {(resource === 'recordings' || resource === 'releases') && <Button variant="outlined"
            disabled={busy} onClick={() => void prepare(candidate.candidateId, candidate.editionId)} sx={{ alignSelf: 'flex-start' }}>
            Prepare review for candidate {candidate.candidateId}{candidate.editionId ? ` edition ${candidate.editionId}` : ''}
          </Button>}
          <Box sx={{ minWidth: 0, width: '100%', overflowX: 'auto', contain: 'inline-size' }}>
            <Table size="small" aria-label={`Enrichment comparison ${candidate.candidateId}`}>
              <TableHead><TableRow><TableCell>Field</TableCell><TableCell>Catalog snapshot</TableCell><TableCell>{providerName} proposal</TableCell><TableCell>Evidence</TableCell></TableRow></TableHead>
              <TableBody>{rows.map(row => <TableRow key={row.field}>
                <TableCell>{row.label}</TableCell><TableCell>{row.current ?? 'Not recorded'}</TableCell>
                <TableCell>{row.proposed ?? 'No proposal'}</TableCell>
                <TableCell>{candidate.evidence.filter(e => e.field === row.field).map((e, index) =>
                  <Typography variant="body2" key={index}>{e.source}{e.sourceRecordId ? `: ${e.sourceRecordId}` : ''}; {e.retrievedAt}{e.attribution ? `; ${e.attribution}` : ''}</Typography>)}</TableCell>
              </TableRow>)}</TableBody>
            </Table>
          </Box>
          {resource === 'releases' && <>
            <Button disabled={busy} sx={{ alignSelf: 'flex-start' }} onClick={() => void lookup(candidate.candidateId)}>
              Fetch details for candidate {candidate.candidateId}
            </Button>
            {metadata.releaseDetails && <>
              <Typography variant="body2">Provider evidence only. {provider === 'sonovault' && 'Choose an edition explicitly to inspect its track numbering.'} No ownership or canonical track matching is inferred.</Typography>
              {provider === 'sonovault' && <Typography variant="body2">MusicBrainz release IDs (may span editions): {metadata.releaseDetails.musicBrainzReleaseIds?.join(', ') || 'Not supplied'}. Release-group IDs: {metadata.releaseDetails.musicBrainzReleaseGroupIds?.join(', ') || 'Not supplied'}.</Typography>}
              {provider === 'sonovault' && !metadata.releaseDetails.editions.length && <Typography>No editions supplied in this response; search results usually require a detail fetch.</Typography>}
              {!!metadata.releaseDetails.editions.length && <Box sx={{ minWidth: 0, width: '100%', overflowX: 'auto', contain: 'inline-size' }}>
                <Table size="small" aria-label={`SonoVault editions ${candidate.candidateId}`}>
                  <TableHead><TableRow><TableCell>Edition</TableCell><TableCell>Source identity</TableCell><TableCell>Format</TableCell><TableCell>Date / precision</TableCell><TableCell>Label / catalog number / barcode</TableCell><TableCell>Tracks</TableCell><TableCell>Inspect</TableCell></TableRow></TableHead>
                  <TableBody>{metadata.releaseDetails.editions.map(edition => <TableRow key={edition.id}>
                    <TableCell>{edition.id}: {edition.title ?? 'Untitled edition'}</TableCell>
                    <TableCell>{edition.source}: {edition.externalId}</TableCell><TableCell>{edition.format ?? 'Not supplied'}</TableCell>
                    <TableCell>{edition.releaseDate ?? 'Not supplied'} / {edition.datePrecision ?? 'Unknown precision'}</TableCell>
                    <TableCell>{edition.labelName ?? 'Not supplied'} / {edition.catalogNumber ?? 'Not supplied'} / {edition.barcode ?? 'Not supplied'}</TableCell>
                    <TableCell>{edition.trackCount ?? 'Not supplied'}</TableCell>
                    <TableCell><Button disabled={busy || metadata.releaseDetails?.selectedEditionId === edition.id}
                      onClick={() => void lookup(candidate.candidateId, edition.id)}>Inspect edition {edition.id}</Button></TableCell>
                  </TableRow>)}</TableBody>
                </Table>
              </Box>}
              {!!metadata.releaseDetails.tracks.length && <Box sx={{ minWidth: 0, width: '100%', overflowX: 'auto', contain: 'inline-size' }}>
                <Typography>{provider === 'beatport' ? 'Beatport release tracklist' : metadata.releaseDetails.selectedEditionId ? `Edition ${metadata.releaseDetails.selectedEditionId} tracklist` : 'Provider consensus tracklist (no edition selected)'}</Typography>
                <Table size="small" aria-label={`${providerName} tracklist ${candidate.candidateId}`}>
                  <TableHead><TableRow><TableCell>Disc / track</TableCell><TableCell>Provider title (may include mix)</TableCell><TableCell>Artists</TableCell><TableCell>ISRC</TableCell><TableCell>{providerName} ID</TableCell></TableRow></TableHead>
                  <TableBody>{metadata.releaseDetails.tracks.map((track, index) => <TableRow key={`${track.id}:${index}`}>
                    <TableCell>{track.discNumber ?? '?'} / {track.trackNumber ?? '?'}</TableCell><TableCell>{track.title}</TableCell>
                    <TableCell>{track.credits.map(c => `${c.name} (${c.role})`).join(', ')}</TableCell><TableCell>{track.isrc ?? 'Not supplied'}</TableCell><TableCell>{track.id}</TableCell>
                  </TableRow>)}</TableBody>
                </Table>
              </Box>}
            </>}
          </>}
        </Stack>
      })}
    </>}
    {review && <ReviewFields key={review.id} review={review} busy={busy} onApply={apply} />}
  </Stack>
}

function display(value: unknown): string {
  if (value === null || value === undefined) return 'Not recorded'
  if (Array.isArray(value)) return value.map(display).join(', ') || 'Not recorded'
  if (typeof value === 'object') return JSON.stringify(value)
  return String(value)
}

function ReviewFields({ review, busy, onApply }: { review: EnrichmentReview; busy: boolean;
  onApply: (fields: string[], preferred: string | null, reason: string, confirmed: boolean) => Promise<void> }) {
  const [selected, setSelected] = useState<string[]>([])
  const [preferred, setPreferred] = useState('')
  const [reason, setReason] = useState('')
  const [confirmed, setConfirmed] = useState(false)
  const choices = review.fields.find(row => row.field === 'PreferredGenre')?.proposed
  const genres = Array.isArray(choices) ? choices.filter((value): value is string => typeof value === 'string') : []
  const disabled = busy || !!review.outcome
  return <Stack component="section" aria-label="Frozen enrichment review" spacing={2} sx={{ minWidth: 0, width: '100%' }}>
    <Typography component="h3" variant="h6">Review selected metadata before applying</Typography>
    <Typography variant="body2">Frozen {review.candidate.providerId === 'beatport' ? 'Beatport' : 'SonoVault'} candidate {review.candidate.candidateId}{review.candidate.editionId ? `, edition ${review.candidate.editionId}` : ''}; {review.createdAt}. Review {review.id} expires after 24 hours.</Typography>
    {review.warnings.map((warning, index) => <Alert severity="warning" key={index}>{warning}</Alert>)}
    <Box sx={{ width: '100%', minWidth: 0, overflowX: 'auto', contain: 'inline-size' }}>
      <Table size="small" aria-label="Fields to apply"><TableHead><TableRow>
        <TableCell>Apply</TableCell><TableCell>Catalog at review</TableCell><TableCell>Frozen proposal</TableCell>
      </TableRow></TableHead><TableBody>{review.fields.map(row => <TableRow key={row.field}>
        <TableCell><FormControlLabel label={row.label} control={<Checkbox disabled={disabled} checked={selected.includes(row.field)}
          onChange={(_, checked) => setSelected(checked ? [...selected, row.field] : selected.filter(field => field !== row.field))} />} /></TableCell>
        <TableCell>{display(row.current)}</TableCell><TableCell>{row.field === 'PreferredGenre'
          ? <TextField select label="Preferred classification" value={preferred} disabled={disabled || !selected.includes(row.field)}
            onChange={event => setPreferred(event.target.value)} sx={{ minWidth: 200 }}>
            <MenuItem value="">Choose a classification</MenuItem>{genres.map(genre => <MenuItem key={genre} value={genre}>{genre}</MenuItem>)}
          </TextField> : display(row.proposed)}</TableCell>
      </TableRow>)}</TableBody></Table>
    </Box>
    <TextField label="Enrichment review reason" value={reason} disabled={disabled} onChange={event => setReason(event.target.value)}
      slotProps={{ htmlInput: { maxLength: 2000 } }} multiline minRows={2} />
    <FormControlLabel label={`I confirm these selected fields belong to this ${review.entityKind}`}
      control={<Checkbox checked={confirmed} disabled={disabled} onChange={(_, checked) => setConfirmed(checked)} />} />
    <Button variant="contained" disabled={disabled || !selected.length || !reason.trim() || !confirmed ||
      selected.includes('PreferredGenre') && !preferred} sx={{ alignSelf: 'flex-start' }}
      onClick={() => void onApply(selected, selected.includes('PreferredGenre') ? preferred : null, reason, confirmed)}>
      Apply selected metadata
    </Button>
    {review.outcome && <Alert severity="success">Applied {review.outcome.fields.join(', ')}. Review {review.outcome.reviewId} was saved with provenance and before/after history.</Alert>}
  </Stack>
}
