/// <reference lib="dom" />

// npm run icons: renders the app's icons and logo from the brand artwork in
// packages/web/brand/ (icon.webp, logo.webp, transparent backgrounds) with headless Chromium:
//   packages/web/public/icons/  icon-192.png, icon-512.png (transparent, for the manifest and
//                               the page), icon-maskable-512.png and apple-touch-icon.png (on the
//                               icon's own dark green, full bleed), favicon-32.png
//   packages/web/public/brand/  logo.png (white lettering outlined in dark green: reads on light
//                               and dark backgrounds alike)
// Run where Playwright's Chromium works, or with ICONS_BROWSER_CHANNEL=chrome to use an installed
// Chrome.

import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import * as path from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from '@playwright/test';

const web = fileURLToPath(new URL('../../packages/web/', import.meta.url));
const source = (name: string) =>
  'data:image/webp;base64,' + readFileSync(path.join(web, 'brand', name)).toString('base64');

/** The icon's own dark green: behind it where a platform wants no transparency. */
const BACKGROUND = '#021a05';

interface Render {
  out: string;
  /** icon: square, the artwork centred; logo: the artwork's own shape at this height. */
  kind: 'icon' | 'logo';
  size: number;
  /** Of the square the artwork may fill (icon only). */
  fill?: number;
  background?: string;
}

const renders: Render[] = [
  { out: 'icons/icon-192.png', kind: 'icon', size: 192, fill: 0.96 },
  { out: 'icons/icon-512.png', kind: 'icon', size: 512, fill: 0.96 },
  { out: 'icons/favicon-32.png', kind: 'icon', size: 32, fill: 1 },
  // Maskable: the platform crops to a circle or squircle; keep the artwork in the safe zone.
  {
    out: 'icons/icon-maskable-512.png',
    kind: 'icon',
    size: 512,
    fill: 0.72,
    background: BACKGROUND,
  },
  // iOS draws its own rounded corners and shows transparency as black: full bleed, the icon's
  // rounded square filling it.
  { out: 'icons/apple-touch-icon.png', kind: 'icon', size: 180, fill: 1, background: BACKGROUND },
  { out: 'brand/logo.png', kind: 'logo', size: 240 },
];

const browser = await chromium.launch({ channel: process.env.ICONS_BROWSER_CHANNEL || undefined });
const page = await browser.newPage();
for (const render of renders) {
  const png = await page.evaluate(
    async ({ src, render }) => {
      const image = new Image();
      image.src = src;
      await image.decode();
      // The artwork's box: everything not (nearly) transparent.
      const probe = document.createElement('canvas');
      probe.width = image.width;
      probe.height = image.height;
      const probing = probe.getContext('2d')!;
      probing.drawImage(image, 0, 0);
      const pixels = probing.getImageData(0, 0, image.width, image.height).data;
      let [left, top, right, bottom] = [image.width, image.height, 0, 0];
      for (let y = 0; y < image.height; y++)
        for (let x = 0; x < image.width; x++)
          if (pixels[(y * image.width + x) * 4 + 3] > 8) {
            left = Math.min(left, x);
            right = Math.max(right, x);
            top = Math.min(top, y);
            bottom = Math.max(bottom, y);
          }
      const [w, h] = [right - left + 1, bottom - top + 1];

      const canvas = document.createElement('canvas');
      const g = canvas.getContext('2d')!;
      g.imageSmoothingQuality = 'high';
      if (render.kind === 'icon') {
        canvas.width = canvas.height = render.size;
        if (render.background) {
          g.fillStyle = render.background;
          g.fillRect(0, 0, render.size, render.size);
        }
        const scale = (render.size * (render.fill ?? 1)) / Math.max(w, h);
        const [dw, dh] = [w * scale, h * scale];
        g.drawImage(image, left, top, w, h, (render.size - dw) / 2, (render.size - dh) / 2, dw, dh);
      } else {
        canvas.height = render.size;
        canvas.width = Math.round((w * render.size) / h);
        g.drawImage(image, left, top, w, h, 0, 0, canvas.width, canvas.height);
      }
      return canvas.toDataURL('image/png');
    },
    { src: source(render.kind === 'icon' ? 'icon.webp' : 'logo.webp'), render },
  );
  const out = path.join(web, 'public', render.out);
  mkdirSync(path.dirname(out), { recursive: true });
  writeFileSync(out, Buffer.from(png.slice(png.indexOf(',') + 1), 'base64'));
  console.log(render.out);
}
await browser.close();
