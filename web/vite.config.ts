/// <reference types="vitest/config" />
import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import tailwindcss from '@tailwindcss/vite';

// In dev the API is the compose stack opened by compose.dev.yml on 127.0.0.1:8080. In production nginx
// proxies /api on the same origin, so the client always uses the relative /api (no build-time variable).
export default defineConfig({
  plugins: [react(), tailwindcss()],
  server: {
    port: 5173,
    strictPort: true,
    proxy: { '/api': { target: 'http://127.0.0.1:8080', changeOrigin: false } },
  },
  test: { environment: 'node', include: ['src/**/*.test.{ts,tsx}'] },
});
