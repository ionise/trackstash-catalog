import { useEffect, useRef, useState } from 'react'
import { Alert, Box, Button, LinearProgress, Stack, Typography } from '@mui/material'
import { useLogoutIfAccessDenied } from 'react-admin'
import { mediaClient, remoteInventoryClient } from './catalog'
import type { EnrichmentLookup, RemoteInventoryKey } from './catalog'
import { MediaSonoVaultImport } from './MediaSonoVaultImport'

type Source = { kind: 'local'; id: number } | { kind: 'remote'; key: RemoteInventoryKey; version: string }

export function MediaProviderSearch({ source, disabled, onBeatportTrack, onSonoBusy, onSonoAdmitted }: {
  source: Source; disabled: boolean; onBeatportTrack?: (id: string) => void
  onSonoBusy: (busy: boolean) => void; onSonoAdmitted: (id: string) => void
}) {
  const [lookup, setLookup] = useState<EnrichmentLookup>()
  const [release, setRelease] = useState<EnrichmentLookup>()
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)
  const pending = useRef<AbortController | undefined>(undefined)
  const logout = useLogoutIfAccessDenied()
  useEffect(() => () => pending.current?.abort(), [])
  async function search(provider: 'beatport' | 'sonovault', searchByMetadata: boolean) {
    if (pending.current) return
    const controller = new AbortController()
    pending.current = controller; setBusy(true); setError(''); setLookup(undefined); setRelease(undefined)
    try {
      const result = source.kind === 'local'
        ? await mediaClient.providerLookup(source.id, provider, searchByMetadata, controller.signal)
        : await remoteInventoryClient.providerLookup(source.key, source.version, provider, searchByMetadata, controller.signal)
      if (!controller.signal.aborted) setLookup(result)
    } catch (failure) {
      if (!controller.signal.aborted) {
        setError(failure instanceof Error ? failure.message : 'Provider lookup failed.')
        void logout(failure)
      }
    } finally {
      if (pending.current === controller) { pending.current = undefined; setBusy(false) }
    }
  }
  async function inspectRelease(id: string, edition?: string) {
    if (pending.current) return
    const controller = new AbortController()
    pending.current = controller; setBusy(true); setError(''); setRelease(undefined)
    try {
      const result = await mediaClient.providerRelease(id, edition, controller.signal)
      if (!controller.signal.aborted) setRelease(result)
    } catch (failure) {
      if (!controller.signal.aborted) {
        setError(failure instanceof Error ? failure.message : 'SonoVault Release lookup failed.')
        void logout(failure)
      }
    } finally {
      if (pending.current === controller) { pending.current = undefined; setBusy(false) }
    }
  }
  return <Box component="section" aria-label="Find provider tracks">
    <Typography variant="h6">Find track metadata</Typography>
    <Typography variant="body2">Search uses a valid ISRC first, otherwise the available title and Artist tags.
      Only track metadata is sent; no path, fingerprint or audio. Candidates are not automatically linked.</Typography>
    <Stack direction="row" spacing={1} useFlexGap sx={{ flexWrap: 'wrap' }}>
      <Button disabled={busy || disabled} onClick={() => void search('beatport', false)}>Search Beatport</Button>
      <Button disabled={busy || disabled} onClick={() => void search('sonovault', false)}>Search SonoVault</Button>
      <Button disabled={busy || disabled} onClick={() => void search('beatport', true)}>Beatport by title and Artist</Button>
      <Button disabled={busy || disabled} onClick={() => void search('sonovault', true)}>SonoVault by title and Artist</Button>
    </Stack>
    {busy && <LinearProgress aria-label="Searching provider tracks" />}
    {error && <Alert severity="error">{error}</Alert>}
    {lookup && <>
      <Typography>{lookup.providerId}: {lookup.candidates.length} candidates; {lookup.query.isrc ? 'searched by ISRC' : 'searched by metadata'}.</Typography>
      {!lookup.candidates.length && <Typography>No candidates found. Try the title and Artist search or adjust the scanner tags.</Typography>}
      {lookup.candidates.map(candidate => <Box key={candidate.candidateId} sx={{ my: 1 }}>
        <Typography>{candidate.metadata.name ?? 'Untitled'} {candidate.metadata.mixName ?? ''} — {
          candidate.metadata.credits?.filter(credit => credit.role === 'primary').map(credit => credit.name).join(', ') || 'Unknown Artist'}</Typography>
        <Typography variant="body2">{candidate.metadata.isrc ?? 'No ISRC'}; {candidate.metadata.bpm ?? '?'} BPM; {candidate.metadata.musicalKey?.value ?? 'unknown key'}</Typography>
        {candidate.match.conflicts.map(conflict => <Alert severity="warning" key={conflict}>{conflict}</Alert>)}
        {lookup.providerId === 'beatport' && onBeatportTrack && <Button disabled={busy || disabled}
          onClick={() => onBeatportTrack(candidate.candidateId)}>Find Release for {candidate.metadata.name ?? candidate.candidateId}</Button>}
        {candidate.reportedRelease?.source === 'sonovault' && <Button disabled={busy || disabled}
          onClick={() => void inspectRelease(candidate.reportedRelease!.id)}>Inspect SonoVault Release {candidate.reportedRelease.title ?? candidate.reportedRelease.id}</Button>}
      </Box>)}
      {lookup.warnings.map(warning => <Typography variant="body2" key={warning}>{warning}</Typography>)}
    </>}
    {release && <Box component="section" aria-label="SonoVault Release evidence" sx={{ mt: 2 }}>
      <Alert severity="info">SonoVault reports a representative Release, which may not be the file's edition. This inspection does not create or link anything.</Alert>
      {!release.candidates.length && <Typography>That SonoVault Release is no longer available.</Typography>}
      {release.candidates.map(candidate => <Box key={candidate.candidateId}>
        <Typography variant="h6">{candidate.metadata.name}</Typography>
        {candidate.metadata.releaseDetails?.editions.map(edition => <Box key={edition.id}>
          <Typography>{edition.title ?? candidate.metadata.name} — {edition.labelName ?? 'Unknown Label'};
            {' '}{edition.catalogNumber ?? 'No catalog number'}; {edition.releaseDate ?? 'No date'} ({edition.source})</Typography>
          <Button disabled={busy || disabled} onClick={() => void inspectRelease(candidate.candidateId, edition.id)}>Inspect edition {edition.id}</Button>
        </Box>)}
        {candidate.metadata.releaseDetails?.tracks.map(track => <Typography key={`${track.id}-${track.discNumber}-${track.trackNumber}`} variant="body2">
          {track.discNumber ?? '?'} / {track.trackNumber ?? '?'}: {track.title} — {track.isrc ?? 'No ISRC'}
        </Typography>)}
      </Box>)}
      {release.warnings.map(warning => <Typography variant="body2" key={warning}>{warning}</Typography>)}
    </Box>}
    <MediaSonoVaultImport key={`${release?.candidates[0]?.candidateId}-${release?.candidates[0]?.editionId}`}
      release={release} disabled={disabled || busy} onBusy={onSonoBusy} onAdmitted={onSonoAdmitted} />
  </Box>
}
