import { defineConfig } from '@playwright/test'

const development = process.env.ADMIN_TEST_DEV === '1'

export default defineConfig({
  testDir: './e2e',
  workers: 1,
  use: { baseURL: development ? 'http://localhost:5173' : 'http://localhost:5199', headless: true },
  webServer: [{
    command: 'node e2e/serve.mjs',
    url: 'http://localhost:5199/admin/',
    reuseExistingServer: false,
    timeout: 120_000,
    gracefulShutdown: { signal: 'SIGTERM', timeout: 10_000 },
  }, ...(development ? [{
    command: 'npm run dev',
    url: 'http://localhost:5173/admin/',
    env: { CatalogApi__Port: '5199' },
    reuseExistingServer: false,
  }] : [])],
})
