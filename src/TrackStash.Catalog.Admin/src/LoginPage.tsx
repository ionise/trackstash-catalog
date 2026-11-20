import { useEffect, useState } from 'react'
import { Alert, Box, Button, Card, CardContent, TextField, Typography } from '@mui/material'
import { useLogin } from 'react-admin'
import { sessionClient } from './catalog'

export function LoginPage() {
  const login = useLogin()
  const [apiKey, setApiKey] = useState('')
  const [username, setUsername] = useState('')
  const [password, setPassword] = useState('')
  const [sessionAvailability, setSessionAvailability] = useState<'checking' | 'available' | 'unavailable' | 'failed'>('checking')
  const [showApiKey, setShowApiKey] = useState(false)
  const [error, setError] = useState('')
  const [pending, setPending] = useState(false)

  useEffect(() => {
    let active = true
    sessionClient.status().then(status => {
      if (active) setSessionAvailability(status.available ? 'available' : 'unavailable')
    }).catch(() => {
      if (active) setSessionAvailability('failed')
    })
    return () => { active = false }
  }, [])

  async function submit(credentials: { apiKey: string } | { username: string; password: string }) {
    setPending(true)
    setError('')
    try {
      await login(credentials)
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : 'Sign-in failed.')
    } finally {
      setApiKey('')
      setUsername('')
      setPassword('')
      setPending(false)
    }
  }

  const sessionAvailable = sessionAvailability === 'available'
  const apiKeyVisible = sessionAvailability !== 'checking' && (!sessionAvailable || showApiKey)

  return (
    <Box sx={{ display: 'flex', justifyContent: 'center', pt: 10, px: 2 }}>
      <Card sx={{ maxWidth: 440, width: '100%' }}>
        <CardContent>
          <Typography variant="h5" component="h1" gutterBottom>TrackStash Catalog</Typography>
          {sessionAvailability === 'checking' && <Typography role="status">Checking sign-in options…</Typography>}
          {sessionAvailable && <>
            <Typography sx={{ mb: 2 }}>Sign in with the catalog administrator account.</Typography>
            <form onSubmit={event => {
              event.preventDefault()
              void submit({ username, password })
            }}>
              <TextField
                fullWidth label="Username" autoComplete="username"
                value={username} onChange={event => setUsername(event.target.value)} required
                disabled={pending}
              />
              <TextField
                fullWidth label="Password" type="password" autoComplete="current-password"
                value={password} onChange={event => setPassword(event.target.value)} required
                disabled={pending} sx={{ mt: 2 }}
              />
              <Button fullWidth type="submit" variant="contained" disabled={pending || !username || !password} sx={{ mt: 2 }}>
                {pending ? 'Signing in...' : 'Sign in'}
              </Button>
            </form>
            <Button
              type="button" size="small" disabled={pending} sx={{ mt: 2 }}
              aria-expanded={showApiKey} aria-controls="catalog-api-key-login"
              onClick={() => {
                if (showApiKey) setApiKey('')
                setShowApiKey(!showApiKey)
              }}
            >
              {showApiKey ? 'Hide API-key sign-in' : 'Sign in with an API key instead'}
            </Button>
          </>}
          {sessionAvailability === 'failed' && <Alert severity="warning" sx={{ mb: 2 }}>
            Could not check whether username/password login is available. You can still try API-key sign-in.
          </Alert>}
          {error && <Alert severity="error" sx={{ mb: 2 }}>{error}</Alert>}
          <Box id="catalog-api-key-login" hidden={!apiKeyVisible} sx={{ mt: sessionAvailable ? 1 : 0 }}>
            {apiKeyVisible && <>
              <Typography sx={{ mb: 2 }}>
                If you have an API key, enter it here. It is kept in memory only and discarded on refresh.
              </Typography>
              <form onSubmit={event => {
                event.preventDefault()
                void submit({ apiKey })
              }}>
                <TextField
                  fullWidth label="API key" type="password" autoComplete="off"
                  value={apiKey} onChange={event => setApiKey(event.target.value)} required
                  disabled={pending}
                />
                <Button fullWidth type="submit" variant={sessionAvailable ? 'outlined' : 'contained'} disabled={pending || !apiKey} sx={{ mt: 2 }}>
                  {pending ? 'Connecting...' : 'Connect'}
                </Button>
              </form>
            </>}
          </Box>
        </CardContent>
      </Card>
    </Box>
  )
}
