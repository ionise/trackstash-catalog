import { useRef, useState } from 'react'
import { Alert, Box, Button, Checkbox, Chip, FormControlLabel, LinearProgress, Stack, Table, TableBody, TableCell, TableHead, TableRow, Typography } from '@mui/material'
import { useLogoutIfAccessDenied } from 'react-admin'
import { Link } from 'react-router-dom'
import { purchaseBatchSize, purchaseReleaseClient } from './catalog'
import type { PurchaseBatchItem } from './catalog'

type Phase = 'idle' | 'checking' | 'checked' | 'admitting' | 'done'
const statusChip: Record<PurchaseBatchItem['status'], { label: string; color: 'success' | 'info' | 'warning' | 'error' }> = {
  admitted: { label: 'Admitted', color: 'success' }, would_admit: { label: 'Will admit', color: 'info' },
  needs_review: { label: 'Needs review', color: 'warning' }, failed: { label: 'Failed', color: 'error' },
}

function Summary({ items }: { items: PurchaseBatchItem[] }) {
  const count = (status: PurchaseBatchItem['status']) => items.filter(item => item.status === status).length
  return <Typography>
    {count('admitted') > 0 && `${count('admitted')} admitted. `}
    {count('would_admit') > 0 && `${count('would_admit')} can be admitted automatically. `}
    {count('needs_review') > 0 && `${count('needs_review')} need individual review. `}
    {count('failed') > 0 && `${count('failed')} failed. `}
  </Typography>
}

/** Checks ready, unadmitted purchase releases against the batch rules and admits the clear-cut ones in chunks. */
export function BatchAdmission({ onAdmitted }: { onAdmitted: () => void }) {
  const logout = useLogoutIfAccessDenied()
  const [phase, setPhase] = useState<Phase>('idle')
  const [items, setItems] = useState<PurchaseBatchItem[]>([])
  const [progress, setProgress] = useState({ done: 0, total: 0 })
  const [confirmed, setConfirmed] = useState(false)
  const [error, setError] = useState<string>()
  const abort = useRef<AbortController | null>(null)

  async function run(ids: number[], dryRun: boolean) {
    const controller = new AbortController(); abort.current = controller
    setError(undefined); setProgress({ done: 0, total: ids.length })
    const results: PurchaseBatchItem[] = []
    try {
      for (let start = 0; start < ids.length && !controller.signal.aborted; start += purchaseBatchSize) {
        const report = await purchaseReleaseClient.admit(ids.slice(start, start + purchaseBatchSize), dryRun, controller.signal)
        results.push(...report.items); setItems([...results]); setProgress({ done: results.length, total: ids.length })
      }
    } catch (failure) {
      if (!controller.signal.aborted) { setError(failure instanceof Error ? failure.message : String(failure)); void logout(failure) }
    } finally { abort.current = null }
    return results
  }
  async function check() {
    setPhase('checking'); setItems([]); setConfirmed(false)
    try {
      const ids = await purchaseReleaseClient.readyIds()
      await run(ids, true)
    } catch (failure) { setError(failure instanceof Error ? failure.message : String(failure)); void logout(failure) }
    setPhase('checked')
  }
  async function admit() {
    const ids = items.filter(item => item.status === 'would_admit').map(item => item.releaseId)
    const others = items.filter(item => item.status !== 'would_admit')
    setPhase('admitting'); setConfirmed(false)
    const results = await run(ids, false)
    setItems([...results, ...others]); setPhase('done'); onAdmitted()
  }

  const busy = phase === 'checking' || phase === 'admitting'
  const admittable = items.filter(item => item.status === 'would_admit').length
  const shown = [...items].sort((a, b) => Number(a.status === 'would_admit' || a.status === 'admitted') - Number(b.status === 'would_admit' || b.status === 'admitted'))
  return <Box component="section" aria-label="Batch admission" sx={{ my: 2, p: 2, border: 1, borderColor: 'divider', borderRadius: 1 }}>
    <Typography variant="h6" component="h2">Batch admit ready releases</Typography>
    <Typography variant="body2" color="text.secondary" sx={{ mb: 1 }}>
      Admits ready, not-yet-admitted releases whose identities are clear-cut: new Beatport artists and recordings are created,
      compound artist names (e.g. “A &amp; B”) are created as unresolved for later splitting, and a recording with the same ISRC and mix is reused.
      Same-name artists without a Beatport ID and uncertain recording matches are left for individual review. Each release is imported separately with the normal checks.
    </Typography>
    <Stack direction="row" spacing={2} useFlexGap sx={{ flexWrap: 'wrap', alignItems: 'center' }}>
      <Button variant="outlined" disabled={busy} onClick={() => void check()}>{phase === 'idle' ? 'Check ready releases' : 'Check again'}</Button>
      {busy && <Button color="warning" onClick={() => abort.current?.abort()}>Stop after this group</Button>}
    </Stack>
    {busy && <Box sx={{ my: 1 }}>
      <LinearProgress aria-label="Batch admission progress" variant={progress.total ? 'determinate' : 'indeterminate'} value={progress.total ? 100 * progress.done / progress.total : 0} />
      <Typography variant="body2">{phase === 'checking' ? 'Checking' : 'Admitting'} {progress.done} of {progress.total || '…'}</Typography>
    </Box>}
    {error && <Alert severity="error" sx={{ my: 1 }}>{error}</Alert>}
    {phase !== 'idle' && !busy && items.length === 0 && !error && <Alert severity="success" sx={{ my: 1 }}>No ready releases are waiting for admission.</Alert>}
    {items.length > 0 && <>
      <Summary items={items} />
      {phase === 'checked' && admittable > 0 && <Stack direction="row" spacing={2} useFlexGap sx={{ my: 1, flexWrap: 'wrap', alignItems: 'center' }}>
        <FormControlLabel control={<Checkbox checked={confirmed} onChange={event => setConfirmed(event.target.checked)} />}
          label={`I have a recent catalog backup and want to admit these ${admittable} releases`} />
        <Button variant="contained" disabled={!confirmed} onClick={() => void admit()}>Admit {admittable} releases</Button>
        <Button component={Link} to="/backups">Backups</Button>
      </Stack>}
      {phase === 'checked' && admittable > 0 && <Typography variant="body2" color="text.secondary">
        New-artist counts in a check assume each release is imported alone; artists shared between releases are created once and reused.
      </Typography>}
      <Table size="small" aria-label="Batch admission results" sx={{ mt: 1 }}>
        <TableHead><TableRow><TableCell>Result</TableCell><TableCell>Release</TableCell><TableCell>Details</TableCell></TableRow></TableHead>
        <TableBody>{shown.map(item => <TableRow key={item.releaseId}>
          <TableCell><Chip size="small" color={statusChip[item.status].color} label={statusChip[item.status].label} /></TableCell>
          <TableCell>{item.catalogReleaseId ? <Link to={`/releases/${encodeURIComponent(item.catalogReleaseId)}/show`}>{item.title}</Link> : item.title} ({item.releaseId})</TableCell>
          <TableCell>{item.reasons.length > 0 ? <ul style={{ margin: 0, paddingLeft: 16 }}>{item.reasons.map(reason => <li key={reason}>{reason}</li>)}</ul>
            : `${item.tracks} tracks; ${item.newArtists} new artists${item.unresolvedArtists ? `, ${item.unresolvedArtists} unresolved` : ''}; ${item.newRecordings} new recordings${item.reusedRecordings ? `, ${item.reusedRecordings} reused` : ''}`}</TableCell>
        </TableRow>)}</TableBody>
      </Table>
    </>}
  </Box>
}
