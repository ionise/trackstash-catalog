import { useEffect, useRef, useState } from 'react'
import { Alert, Box, Button, LinearProgress, Stack, Table, TableBody, TableCell, TableHead, TableRow, Typography } from '@mui/material'
import { useLogoutIfAccessDenied } from 'react-admin'
import { trackClient } from './catalog'
import type { EnrichmentLookup } from './catalog'

export function RecordingEnrichment({ id }: { id: string }) {
  const [result, setResult] = useState<EnrichmentLookup>()
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)
  const pending = useRef<AbortController | undefined>(undefined)
  const logout = useLogoutIfAccessDenied()
  useEffect(() => () => pending.current?.abort(), [id])

  async function lookup() {
    if (pending.current) return
    const controller = new AbortController()
    pending.current = controller
    setBusy(true); setError(''); setResult(undefined)
    try {
      const data = await trackClient.enrichment(id, controller.signal)
      if (!controller.signal.aborted) setResult(data)
    } catch (failure) {
      if (!controller.signal.aborted) {
        setError(failure instanceof Error ? failure.message : 'SonoVault lookup failed.')
        void logout(failure)
      }
    } finally {
      if (!controller.signal.aborted) { setBusy(false); pending.current = undefined }
    }
  }

  return <Stack component="section" aria-label="SonoVault enrichment" spacing={2} sx={{ minWidth: 0 }}>
    <Typography component="h2" variant="h6">Metadata enrichment</Typography>
    <Alert severity="info">Read-only pilot: compare SonoVault candidates with this Recording. Nothing is saved or applied. Each click may use one API request; there are no automatic retries.</Alert>
    <Button variant="outlined" disabled={busy} onClick={() => void lookup()} sx={{ alignSelf: 'flex-start' }}>Find enrichment on SonoVault</Button>
    {busy && <LinearProgress aria-label="Looking up SonoVault metadata" />}
    {error && <Alert severity="error">{error}</Alert>}
    {result && <>
      <Typography variant="body2">Lookup {result.retrievedAt}; catalog snapshot {result.query.catalogVersion}. Comparison is against the snapshot used for this lookup.</Typography>
      {result.warnings.map((warning, index) => <Alert key={index} severity="warning">{warning}</Alert>)}
      {!result.candidates.length && <Alert severity="info">No SonoVault candidates found. The lookup succeeded; no catalog changes were made.</Alert>}
      {result.candidates.map(candidate => {
        const metadata = candidate.metadata
        const rows: { field: string; label: string; current: string | number | null; proposed: string | number | null }[] = [
          { field: 'Name', label: 'Title', current: result.query.name, proposed: metadata.name },
          { field: 'MixName', label: 'Mix / version', current: result.query.mixName, proposed: metadata.mixName },
          { field: 'Isrc', label: 'ISRC', current: result.query.isrc, proposed: metadata.isrc },
          { field: 'DurationMs', label: 'Duration (ms)', current: result.query.durationMs, proposed: metadata.durationMs },
          { field: 'Bpm', label: 'BPM', current: 'Not exposed by the current Recording model', proposed: metadata.bpm },
          { field: 'MusicalKey', label: 'Musical key', current: 'Not exposed by the current Recording model', proposed: metadata.musicalKey ? `${metadata.musicalKey.value} (${metadata.musicalKey.notation})` : null },
          { field: 'Genre', label: 'Genre', current: 'Not exposed by the current Recording model', proposed: metadata.genres?.join(', ') || metadata.genre },
          { field: 'Subgenre', label: 'Subgenre', current: 'Not exposed by the current Recording model', proposed: metadata.subgenres?.join(', ') || null },
          { field: 'Credits', label: 'Artist credits', current: `Primary Recording artists: ${result.query.artistNames.join(', ') || 'Not recorded'}`, proposed: metadata.credits?.map(c => `${c.name} (${c.role})`).join(', ') || null },
          { field: 'ExternalReferences', label: 'Identifiers', current: 'See Recording provider identities', proposed: metadata.externalReferences?.map(r => `${r.source}: ${r.externalId}`).join(', ') || null },
        ]
        return <Stack key={`${candidate.providerId}:${candidate.candidateId}:${candidate.editionId ?? ''}`} spacing={1}>
          <Typography component="h3" variant="h6">SonoVault candidate {candidate.candidateId}{candidate.editionId ? ` / edition ${candidate.editionId}` : ''}</Typography>
          <Typography>Match basis: {candidate.match.basis}. {candidate.match.explanation}</Typography>
          {candidate.match.conflicts.map((conflict, index) => <Alert severity="warning" key={index}>{conflict}</Alert>)}
          <Box sx={{ minWidth: 0, width: '100%', overflowX: 'auto', contain: 'inline-size' }}>
            <Table size="small" aria-label={`Enrichment comparison ${candidate.candidateId}`}>
              <TableHead><TableRow><TableCell>Field</TableCell><TableCell>Catalog snapshot</TableCell><TableCell>SonoVault proposal</TableCell><TableCell>Evidence</TableCell></TableRow></TableHead>
              <TableBody>{rows.map(row => <TableRow key={row.field}>
                <TableCell>{row.label}</TableCell><TableCell>{row.current ?? 'Not recorded'}</TableCell>
                <TableCell>{row.proposed ?? 'No proposal'}</TableCell>
                <TableCell>{candidate.evidence.filter(e => e.field === row.field).map((e, index) =>
                  <Typography variant="body2" key={index}>{e.source}{e.sourceRecordId ? `: ${e.sourceRecordId}` : ''}; {e.retrievedAt}{e.attribution ? `; ${e.attribution}` : ''}</Typography>)}</TableCell>
              </TableRow>)}</TableBody>
            </Table>
          </Box>
        </Stack>
      })}
    </>}
  </Stack>
}
