import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

const apiTarget = process.env.SHARPLABNEXT_DEV_API_TARGET ?? 'http://localhost:5000'

export default defineConfig({
  plugins: [react()],
  server: {
    proxy: {
      '/api': {
        target: apiTarget,
        changeOrigin: true,
        ws: true,
      },
    },
  },
  preview: {
    proxy: {
      '/api': {
        target: apiTarget,
        changeOrigin: true,
        ws: true,
      },
    },
  },
})
