import { fileURLToPath } from 'node:url';
import { defineConfig, type Plugin } from 'vite';

/** The C# server that `npm run dev` starts next to Vite (server/src/GreenGlow.Server). */
const SERVER = process.env.GREENGLOW_SERVER_URL ?? 'http://localhost:5080';
const page = (name: string) => fileURLToPath(new URL(name, import.meta.url));

interface Brand {
  name: string | null;
  shortName: string | null;
  color: string | null;
  icon: boolean;
  version: string;
}

const escape = (text: string) => text.replace(/[&<>"']/g, (c) => `&#${c.charCodeAt(0)};`);

/**
 * In development, Vite serves the pages, so it brands them as the server does when it serves
 * the build (server/src/GreenGlow.Server/Branding/BrandPages.cs): the brand an administrator set
 * on the admin page, asked of the server at each page load.
 */
function branding(): Plugin {
  return {
    name: 'greenglow-branding',
    apply: 'serve',
    async transformIndexHtml(html) {
      let brand: Brand;
      try {
        brand = (await (await fetch(`${SERVER}/branding/brand.json`)).json()) as Brand;
      } catch {
        return html; // The server isn't up yet: GreenGlow's own brand.
      }
      if (brand.name) html = html.replaceAll('GreenGlow Connect', escape(brand.name));
      if (brand.shortName)
        html = html.replaceAll('content="GreenGlow"', `content="${escape(brand.shortName)}"`);
      if (brand.color) {
        html = html.replaceAll('content="#1d4d2c"', `content="${brand.color}"`);
        html = html.replace(
          '</head>',
          `<link rel="stylesheet" href="branding/brand.css?v=${brand.version}" />\n  </head>`,
        );
      }
      if (brand.icon)
        html = html.replace(
          /"\/icons\/([a-z0-9-]+\.png)"/g,
          (_, file: string) => `"branding/${file}?v=${brand.version}"`,
        );
      return html;
    },
  };
}

export default defineConfig({
  plugins: [branding()],
  // Relative: the built pages work under any base path (Server__PublicUrl with a path).
  base: './',
  build: {
    rollupOptions: {
      input: {
        main: page('index.html'),
        admin: page('admin.html'),
        help: page('help.html'),
        demo: page('demo.html'),
        setup: page('setup.html'),
        signin: page('signin.html'),
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
      '/branding': SERVER,
      '/ws': { target: SERVER, ws: true },
    },
    // Vite's defaults, plus raw recordings: /@fs/ must not serve them.
    fs: { deny: ['.env', '.env.*', '*.{crt,pem}', '**/.git/**', '**/test/recordings/**'] },
  },
});
