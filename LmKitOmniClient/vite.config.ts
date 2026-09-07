import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'
import tailwindcss from '@tailwindcss/vite'
import { fileURLToPath, URL } from 'node:url'
import Components from 'unplugin-vue-components/vite'
import { PrimeVueResolver } from '@primevue/auto-import-resolver'

// https://vite.dev/config/
export default defineConfig({
  plugins: [
    vue(),
    tailwindcss(),
    Components({
      resolvers: [
        PrimeVueResolver()
      ]
    })
  ],
  resolve: {
    alias: {
      '@': fileURLToPath(new URL('./src', import.meta.url))
    }
  },
  // Keep these rules mirroring LmKitOmniClient/nginx.conf: a prefix proxied here but
  // not there (or vice versa) is a route that works under `npm run dev` and 404s in
  // production. /api is the only prefix the API serves; chat streaming is SSE over it,
  // which needs no `ws: true` (a dead `/hubs` websocket proxy for a SignalR hub the API
  // never registered used to live here and was removed).
  server: {
    proxy: {
      '/api': 'http://localhost:5032',
    },
  },
})
