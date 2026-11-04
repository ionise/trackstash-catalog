import { useEffect, useRef, useState } from 'react'
import { Alert, Box, Button, LinearProgress, Stack, Typography } from '@mui/material'
import { Title, useLogoutIfAccessDenied } from 'react-admin'
import { backupsClient } from './catalog'
import type { BackupInfo, BackupJob, BackupList } from './catalog'

declare global {
  interface Window {
    showSaveFilePicker?: (options: { suggestedName: string; types: { description: string; accept: Record<string, string[]> }[] }) =>
      Promise<{ createWritable(): Promise<WritableStream<Uint8Array>> }>
  }
}

export function Backups() {
  const [list, setList] = useState<BackupList>()
  const [job, setJob] = useState<BackupJob>()
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)
  const [downloading, setDownloading] = useState('')
  const abort = useRef<AbortController | undefined>(undefined)
  const logout = useLogoutIfAccessDenied()
  useEffect(() => {
    const controller = new AbortController()
    void backupsClient.list(controller.signal).then(setList).catch(failure => {
      if (!controller.signal.aborted) {
        setError(failure instanceof Error ? failure.message : 'Backups could not be loaded.')
        void logout(failure)
      }
    })
    return () => { controller.abort(); abort.current?.abort() }
  }, [logout])
  const active = job?.status === 'queued' || job?.status === 'running'
  useEffect(() => {
    if (!active || !job) return
    const controller = new AbortController()
    const timer = setTimeout(() => {
      void backupsClient.job(job.id, controller.signal).then(async next => {
        const completedList = next.status === 'completed' ? await backupsClient.list(controller.signal) : undefined
        if (controller.signal.aborted) return
        setJob(next)
        if (completedList) setList(completedList)
      }).catch(failure => {
        if (!controller.signal.aborted) {
          setError(failure instanceof Error ? failure.message : 'Backup job status could not be loaded.')
          setJob(undefined)
          void logout(failure)
        }
      })
    }, 1000)
    return () => { clearTimeout(timer); controller.abort() }
  }, [job, active, logout])

  async function create() {
    setBusy(true); setError('')
    try { setJob(await backupsClient.start()) }
    catch (failure) {
      setError(failure instanceof Error ? failure.message : 'Backup could not be started.')
      await logout(failure)
    } finally { setBusy(false) }
  }
  async function download(backup: BackupInfo) {
    setError(''); setDownloading(backup.id)
    const controller = new AbortController()
    abort.current = controller
    let writable: WritableStream<Uint8Array> | undefined
    try {
      if (window.showSaveFilePicker) {
        const handle = await window.showSaveFilePicker({
          suggestedName: backup.filename, types: [{ description: 'ZIP backup', accept: { 'application/zip': ['.zip'] } }],
        })
        writable = await handle.createWritable()
      } else if (backup.bytes > 128 * 1024 * 1024) {
        throw new Error('This browser cannot stream to a chosen file. Use a browser with Save File support or retrieve the saved ZIP from the configured backup folder. Browser-memory downloads are limited to 128 MiB.')
      }
      const response = await backupsClient.download(backup.id, controller.signal)
      if (writable) {
        if (!response.body) throw new Error('Backup response has no download stream.')
        const destination = writable
        writable = undefined
        await response.body.pipeTo(destination, { signal: controller.signal })
      } else {
        const blob = await response.blob()
        const url = URL.createObjectURL(blob)
        const anchor = document.createElement('a')
        anchor.href = url; anchor.download = backup.filename
        anchor.click()
        setTimeout(() => URL.revokeObjectURL(url), 60_000)
      }
    } catch (failure) {
      if (writable) await writable.abort(failure)
      if (!(failure instanceof DOMException && failure.name === 'AbortError')) {
        setError(failure instanceof Error ? failure.message : 'Backup download failed.')
        await logout(failure)
      }
    } finally { setDownloading(''); abort.current = undefined }
  }
  return <Box sx={{ p: { xs: 2, md: 4 } }}>
    <Title title="Backups" />
    <Stack spacing={2}>
      <Typography variant="h4" component="h1">Catalog backups</Typography>
      <Typography>Create a verified, timestamped ZIP on the server filesystem containing the catalog and all retained artwork. Download exports that same saved archive.</Typography>
      <Alert severity="info">The newest {list?.retainNewest ?? 10} verified backups per catalog are retained. Older recognised backups are removed only after a new backup succeeds. Downloads block backup/pruning until finished. A download alone does not establish a NAS backup.</Alert>
      <Typography variant="body2">The destination is configured on the server. Credentials and unrelated media files are not included. Restore is available through the CLI into a new directory only. Completed ZIPs survive server restarts; job progress does not. Refresh the list to see CLI-created backups.</Typography>
      {error && <Alert severity="error">{error}</Alert>}
      <Stack direction="row" spacing={2}>
        <Button variant="contained" disabled={busy || active || Boolean(downloading)} onClick={() => void create()}>Create backup</Button>
        <Button disabled={busy || active || Boolean(downloading)} onClick={() => {
          setError('')
          void backupsClient.list().then(setList).catch(failure => {
            setError(failure instanceof Error ? failure.message : 'Backups could not be loaded.')
            void logout(failure)
          })
        }}>Refresh backups</Button>
      </Stack>
      {active && <LinearProgress aria-label="Backup progress" />}
      {job && <Alert severity={job.status === 'failed' ? 'error' : job.status === 'completed' ? 'success' : 'info'}>
        {job.error ?? job.stage}{job.backup ? `: ${job.backup.filename}` : ''}
      </Alert>}
      {job?.warnings?.map(warning => <Alert key={warning} severity="warning">{warning}</Alert>)}
      {list?.items.length === 0 && <Typography>No saved backups for this catalog.</Typography>}
      {list?.items.map(backup => <Box key={backup.id} sx={{ border: 1, borderColor: 'divider', borderRadius: 1, p: 2, overflowWrap: 'anywhere' }}>
        <Typography>{backup.filename}</Typography>
        <Typography variant="body2" color="text.secondary">{new Date(backup.createdUtc).toLocaleString()} / {(backup.bytes / (1024 * 1024)).toFixed(2)} MiB</Typography>
        <Button disabled={busy || active || Boolean(downloading)} onClick={() => void download(backup)}>
          {downloading === backup.id ? 'Downloading ZIP...' : 'Download ZIP'}
        </Button>
      </Box>)}
    </Stack>
  </Box>
}
