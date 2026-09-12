import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

// The built app is served by the API, on the same origin. Same origin is what makes the session
// cookie work without CORS and what makes the antiforgery token readable, so changing it is a
// bigger decision than it looks.
//
// WHERE it is served from, and which port the dev proxy talks to, are the target's business rather
// than this file's: ASP.NET Core serves static files out of wwwroot and listens on 5000, a Node
// host serves a directory it is given and listens on 5000 too, and a third target will want
// something else again. Both are substituted at build time, which is what keeps this one shared
// file honest for every stack instead of quietly describing whichever one was written first.
export default defineConfig({
  plugins: [vue()],
  build: {
    outDir: '{{WebOutDir}}',
    emptyOutDir: true,
  },
  server: {
    // Only for `npm run dev`. In production there is no proxy: one origin serves both.
    proxy: {
      '/api': '{{DevApiOrigin}}',
    },
  },
})
