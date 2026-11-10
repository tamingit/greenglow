import { fileURLToPath } from 'node:url';
import { defineConfig } from 'vite';

/** The C# server that `npm run dev` starts next to Vite (server/src/GreenGlow.Server). */
const SERVER = process.env.GREENGLOW_SERVER_URL ?? 'http://localhost:5080';
const page = (name: string) => fileURLToPath(new URL(name, import.meta.url));

export default defineConfig({
  build: {
    rollupOptions: {
      input: {
        main: page('index.html'),
        admin: page('admin.html'),
        help: page('help.html'),
        demo: page('demo.html'),
      },
    },
  },
  server: {
    port: 5173,
    strictPort: true,
    // The page, /api and /ws share one origin, as they do when the server serves the build.
    // The Origin header passes through unchanged, so the server's allowlist still applies.
    proxy: {
      '/api': SERVER,
      '/auth': SERVER,
      '/healthz': SERVER,
      '/ws': { target: SERVER, ws: true },
    },
    // Vite's defaults, plus raw recordings: /@fs/ must not serve them.
    fs: { deny: ['.env', '.env.*', '*.{crt,pem}', '**/.git/**', '**/test/recordings/**'] },
  },
});
