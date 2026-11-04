import { useState } from 'react'
import { Alert, Box, Button, Card, CardContent, TextField, Typography } from '@mui/material'
import { useLogin } from 'react-admin'

export function LoginPage() {
  const login = useLogin()
  const [apiKey, setApiKey] = useState('')
  const [error, setError] = useState('')
  const [pending, setPending] = useState(false)

  return (
    <Box sx={{ display: 'flex', justifyContent: 'center', pt: 10 }}>
      <Card sx={{ maxWidth: 440 }}>
        <CardContent>
          <Typography variant="h5" component="h1" gutterBottom>TrackStash Catalog</Typography>
          <Typography sx={{ mb: 2 }}>Enter your local API key. It is kept in memory only; refreshing this page signs you out.</Typography>
          <form onSubmit={async event => {
            event.preventDefault()
            setPending(true)
            setError('')
            try {
              await login({ apiKey })
              setApiKey('')
            } catch (failure) {
              setApiKey('')
              setError(failure instanceof Error ? failure.message : 'Sign-in failed.')
            } finally {
              setPending(false)
            }
          }}>
            <TextField
              fullWidth label="API key" type="password" autoComplete="off"
              value={apiKey} onChange={event => setApiKey(event.target.value)} required
              disabled={pending}
            />
            {error && <Alert severity="error" sx={{ mt: 2 }}>{error}</Alert>}
            <Button fullWidth type="submit" variant="contained" disabled={pending || !apiKey} sx={{ mt: 2 }}>
              {pending ? 'Connecting...' : 'Connect'}
            </Button>
          </form>
        </CardContent>
      </Card>
    </Box>
  )
}
