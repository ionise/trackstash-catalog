import { useCallback, useEffect, useRef, useState } from 'react'
import { Alert, Box, Button, Chip, LinearProgress, Paper, Stack, TextField, Typography } from '@mui/material'
import CloudDownload from '@mui/icons-material/CloudDownload'
import { useLogoutIfAccessDenied } from 'react-admin'
import { beatportClient } from './catalog'
import type { BeatportBackfillJob, BeatportBackfillResult, BeatportConnection } from './catalog'

const message = (failure: unknown, fallback: string) => failure instanceof Error ? failure.message : fallback

function SignInForm({ onSignedIn }: { onSignedIn: (connection: BeatportConnection) => void }) {
  const [username, setUsername] = useState('')
  const [password, setPassword] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  return <Stack component="form" direction="row" spacing={1} useFlexGap sx={{ flexWrap: 'wrap', alignItems: 'center', mt: 1 }}
    aria-label="Sign in to Beatport" onSubmit={event => {
      event.preventDefault()
      setBusy(true); setError('')
      void beatportClient.signIn(username, password).then(connection => {
        setPassword(''); onSignedIn(connection)
      }).catch(failure => setError(message(failure, 'Beatport sign-in failed.'))).finally(() => setBusy(false))
    }}>
    <TextField size="small" label="Beatport username" autoComplete="username" value={username} onChange={event => setUsername(event.target.value)} required />
    <TextField size="small" label="Beatport password" type="password" autoComplete="current-password" value={password}
      onChange={event => setPassword(event.target.value)} required />
    <Button type="submit" variant="contained" disabled={busy || !username || !password}>Sign in</Button>
    {error && <Alert severity="error" sx={{ flexBasis: '100%' }}>{error}</Alert>}
  </Stack>
}

function BulkBackfill({ onFinished }: { onFinished: () => void }) {
  const [job, setJob] = useState<BeatportBackfillJob>()
  const [error, setError] = useState('')
  const running = job?.status === 'queued' || job?.status === 'running'
  const jobId = job?.id
  useEffect(() => {
    if (!running || !jobId) return
    const controller = new AbortController()
    const timer = setInterval(() => {
      void beatportClient.job(jobId, controller.signal).then(next => { if (!controller.signal.aborted) setJob(next) }).catch(failure => { if (!controller.signal.aborted) setError(message(failure, 'Backfill progress unavailable.')) })
    }, 1000)
    return () => { clearInterval(timer); controller.abort() }
  }, [running, jobId])
  const reported = useRef<string>(undefined)
  useEffect(() => {
    if (job && !running && reported.current !== job.id) { reported.current = job.id; onFinished() }
  }, [job, running, onFinished])
  const percent = job?.totalReleases ? Math.round(100 * job.processedReleases / job.totalReleases) : 0
  return <Box sx={{ mt: 1 }}>
    <Stack direction="row" spacing={1} useFlexGap sx={{ flexWrap: 'wrap', alignItems: 'center' }}>
      <Button variant="outlined" startIcon={<CloudDownload />} disabled={running} onClick={() => {
        setError('')
        void beatportClient.startJob().then(setJob).catch(failure => setError(message(failure, 'Backfill could not start.')))
      }}>Fetch missing data for all incomplete releases</Button>
      {running && jobId && <Button color="warning" onClick={() => void beatportClient.cancelJob(jobId).catch(() => undefined)}>Stop</Button>}
    </Stack>
    {job && <Box sx={{ mt: 1 }} aria-live="polite">
      {running && <LinearProgress variant={job.totalReleases ? 'determinate' : 'indeterminate'} value={percent} aria-label="Beatport backfill progress" />}
      <Typography variant="body2">
        {job.status === 'queued' ? 'Starting…' : `${job.processedReleases} of ${job.totalReleases} incomplete releases checked`}
        {' '}· {job.completedReleases} now complete · {job.tracksFetched} tracks fetched
        {job.tracksNotFound ? ` · ${job.tracksNotFound} no longer on Beatport` : ''}
        {running && job.currentReleaseId ? ` · working on Beatport ${job.currentReleaseId}` : ''}
      </Typography>
      {job.status === 'completed' && <Alert severity="success" sx={{ mt: 1 }}>
        Backfill finished. {job.stillIncomplete?.length ? `${job.stillIncomplete.length} releases still need attention (see their diagnostics).` : 'Every release is now complete.'}
      </Alert>}
      {(job.status === 'failed' || job.status === 'cancelled') && <Alert severity={job.status === 'failed' ? 'error' : 'warning'} sx={{ mt: 1 }}>
        {job.error ?? 'Backfill stopped.'} Progress so far is kept; start again to continue.
      </Alert>}
    </Box>}
    {error && <Alert severity="error" sx={{ mt: 1 }}>{error}</Alert>}
  </Box>
}

/** Beatport broker connection, sign-in and bulk backfill of incomplete purchase releases. */
export function BeatportPanel({ onSourceChanged }: { onSourceChanged: () => void }) {
  const [connection, setConnection] = useState<BeatportConnection>()
  const [error, setError] = useState('')
  const logout = useLogoutIfAccessDenied()
  const load = useCallback((signal?: AbortSignal) => {
    void beatportClient.connection(signal).then(result => { if (!signal?.aborted) setConnection(result) }).catch(failure => {
      if (!signal?.aborted) { setError(message(failure, 'Beatport status unavailable.')); void logout(failure) }
    })
  }, [logout])
  useEffect(() => {
    const controller = new AbortController()
    load(controller.signal)
    return () => controller.abort()
  }, [load])
  if (error) return <Alert severity="error" sx={{ my: 2 }}>{error}</Alert>
  if (!connection) return <LinearProgress aria-label="Loading Beatport connection" />
  return <Paper variant="outlined" component="section" aria-label="Beatport connection" sx={{ p: 2, my: 2 }}>
    <Stack direction="row" spacing={1} sx={{ alignItems: 'center' }}>
      <Typography variant="h6" component="h2">Beatport</Typography>
      {!connection.configured ? <Chip size="small" label="Not configured" />
        : !connection.reachable ? <Chip size="small" color="error" label="Broker not running" />
        : connection.signedIn ? <Chip size="small" color="success" label={`Signed in as ${connection.username ?? 'unknown'}`} />
        : <Chip size="small" color="warning" label="Signed out" />}
    </Stack>
    {!connection.configured && <Typography variant="body2" sx={{ mt: 1 }}>Start TrackStash with start-prod.sh to run the Beatport broker alongside the catalog.</Typography>}
    {connection.configured && !connection.reachable && <Stack direction="row" spacing={1} sx={{ mt: 1, alignItems: 'center' }}>
      <Typography variant="body2">The broker is not answering. Restart TrackStash with start-prod.sh.</Typography>
      <Button size="small" onClick={() => load()}>Check again</Button>
    </Stack>}
    {connection.reachable && !connection.signedIn && <>
      <Typography variant="body2" sx={{ mt: 1 }}>Sign in once to fill in missing release and track details. Your password goes only to the local broker; it keeps a renewable sign-in token in the macOS Keychain.</Typography>
      <SignInForm onSignedIn={setConnection} />
    </>}
    {connection.reachable && connection.signedIn && <>
      <Typography variant="body2" sx={{ mt: 1 }}>
        {connection.cachedEntities} Beatport items cached · {connection.upstreamRequests} requests to Beatport since the broker started
        {connection.throttled ? ` · slowed down ${connection.throttled}×` : ''}
      </Typography>
      <BulkBackfill onFinished={onSourceChanged} />
      <Button size="small" sx={{ mt: 1 }} onClick={() => void beatportClient.signOut().then(() => load()).catch(failure => setError(message(failure, 'Sign-out failed.')))}>
        Sign out of Beatport
      </Button>
    </>}
  </Paper>
}

/** Fetches one release's missing or invalid Beatport data; the summary is handed to the parent because the inspection reloads afterwards. */
export function BeatportFetchButton({ releaseId, onFetched, disabled = false, onBusyChange }: {
  releaseId: number; onFetched: (summary: string, outcome: BeatportBackfillResult) => void
  disabled?: boolean; onBusyChange?: (busy: boolean) => void
}) {
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  return <Box sx={{ my: 1 }}>
    <Button variant="contained" size="small" startIcon={<CloudDownload />} disabled={busy || disabled} onClick={() => {
      setBusy(true); onBusyChange?.(true); setError('')
      void beatportClient.backfillRelease(releaseId).then(outcome => {
        onFetched(outcome.sourceCompleteAfter
          ? `Fetched ${outcome.tracksFetched} track${outcome.tracksFetched === 1 ? '' : 's'}${outcome.releaseRefreshed ? ' and the release' : ''}; the source is now complete.`
          : `Fetched ${outcome.tracksFetched} of ${outcome.tracksRequested} tracks${outcome.tracksNotFound ? ` (${outcome.tracksNotFound} no longer on Beatport)` : ''}. Still blocked: ${outcome.remainingBlockers.join(', ').replaceAll('_', ' ')}.`, outcome)
      }).catch(failure => setError(message(failure, 'Beatport fetch failed.'))).finally(() => { setBusy(false); onBusyChange?.(false) })
    }}>{busy ? 'Fetching from Beatport…' : 'Fetch missing from Beatport'}</Button>
    {busy && <LinearProgress sx={{ mt: 1 }} aria-label="Fetching from Beatport" />}
    {error && <Alert severity="error" sx={{ mt: 1 }}>{error}</Alert>}
  </Box>
}
