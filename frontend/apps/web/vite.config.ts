import { fileURLToPath, URL } from 'node:url'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'
import { defineConfig } from 'vite'

const gateway = process.env.VITE_GATEWAY_URL ?? 'http://localhost:5000'

export default defineConfig({
  plugins: [react(), tailwindcss()],
  resolve: {
    alias: {
      '@shared': fileURLToPath(new URL('../../shared', import.meta.url)),
      '@features': fileURLToPath(new URL('../../features', import.meta.url)),
    },
  },
  server: {
    port: 5173,
    fs: { allow: ['../..'] },
    // Browser talks to one origin; the gateway fans out to services.
    proxy: { '/health': gateway },
  },
})
