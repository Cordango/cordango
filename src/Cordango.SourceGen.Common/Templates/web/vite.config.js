import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

// The built app is served by the API from its wwwroot, on the same origin. Same origin is what
// makes the session cookie work without CORS and what makes the antiforgery token readable, so
// changing it is a bigger decision than it looks.
export default defineConfig({
  plugins: [vue()],
  build: {
    outDir: '../api/wwwroot',
    emptyOutDir: true,
  },
  server: {
    // Only for `npm run dev`. In production there is no proxy: one origin serves both.
    proxy: {
      '/api': 'http://localhost:5000',
    },
  },
})
