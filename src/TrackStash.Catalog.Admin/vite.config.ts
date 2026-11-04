import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'
import { fileURLToPath } from 'node:url'

export default defineConfig({
  plugins: [react()],
  base: '/admin/',
  resolve: {
    alias: {
      'decode-uri-component': fileURLToPath(new URL('./tools/uri-decoder.cjs', import.meta.url)),
    },
  },
  server: {
    host: 'localhost',
    port: 5173,
    strictPort: true,
    proxy: {
      '/api': { target: `http://localhost:${process.env.CatalogApi__Port ?? '5187'}` },
    },
  },
})
