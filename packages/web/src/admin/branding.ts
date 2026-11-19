// Branding: the name, short name, brand colour and icon every page shows, and the address
// people open the app at. The server serves every page with the brand in place; this page
// updates itself as soon as a change is saved, and other pages show it at their next load.
//
// A custom icon is drawn here from the image an administrator picks, into the same files and
// sizes as the built icons (tools/src/icons.ts makes those), and the server only checks them.

import { api } from './api.ts';
import { button, h, notify } from './dom.ts';

interface Address {
  current: string;
  /** Saved here or at setup; null when the server's Server__PublicUrl is in force. */
  saved: string | null;
  configured: string;
}

export interface BrandingInfo {
  name: string;
  shortName: string;
  color: string;
  icon: boolean;
  version: string;
  defaults: { name: string; shortName: string; color: string };
  iconSizes: Record<string, number>;
  iconMaxBytes: number;
  address: Address;
}

interface IconRender {
  size: number;
  /** Of the square the artwork may fill. */
  fill: number;
  /** Behind it, for platforms that want no transparency: the brand colour. */
  background?: boolean;
}

/** As tools/src/icons.ts draws the built icons, but a picked image needn't be a rounded square. */
const RENDERS: Record<string, IconRender> = {
  'favicon-32.png': { size: 32, fill: 1 },
  'icon-192.png': { size: 192, fill: 0.96 },
  'icon-512.png': { size: 512, fill: 0.96 },
  // Maskable: the platform crops to a circle or squircle; keep the artwork in the safe zone.
  'icon-maskable-512.png': { size: 512, fill: 0.72, background: true },
  // iOS draws its own rounded corners and shows transparency as black.
  'apple-touch-icon.png': { size: 180, fill: 0.86, background: true },
};

async function loadImage(source: string): Promise<HTMLImageElement> {
  const image = new Image();
  image.src = source;
  await image.decode();
  return image;
}

/** A picked file as a data: URL: the page's CSP allows images from data:, not blob:. */
function dataUrl(file: File): Promise<string> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => resolve(reader.result as string);
    reader.onerror = () => reject(reader.error ?? new Error('The file couldn’t be read.'));
    reader.readAsDataURL(file);
  });
}

/** The image's artwork, trimmed of transparent edges, on a canvas no bigger than 1024 pixels. */
function artwork(image: HTMLImageElement): HTMLCanvasElement {
  // An SVG with no size of its own decodes as 0×0: draw it square.
  const [width, height] =
    image.naturalWidth > 0 ? [image.naturalWidth, image.naturalHeight] : [512, 512];
  const scale = Math.min(1, 1024 / Math.max(width, height));
  const probe = document.createElement('canvas');
  probe.width = Math.max(1, Math.round(width * scale));
  probe.height = Math.max(1, Math.round(height * scale));
  const g = probe.getContext('2d')!;
  g.drawImage(image, 0, 0, probe.width, probe.height);
  const pixels = g.getImageData(0, 0, probe.width, probe.height).data;
  let [left, top, right, bottom] = [probe.width, probe.height, -1, -1];
  for (let y = 0; y < probe.height; y++)
    for (let x = 0; x < probe.width; x++)
      if (pixels[(y * probe.width + x) * 4 + 3] > 8) {
        left = Math.min(left, x);
        right = Math.max(right, x);
        top = Math.min(top, y);
        bottom = Math.max(bottom, y);
      }
  if (right < 0) throw new Error('The image is empty: every pixel is transparent.');
  const trimmed = document.createElement('canvas');
  trimmed.width = right - left + 1;
  trimmed.height = bottom - top + 1;
  trimmed
    .getContext('2d')!
    .drawImage(
      probe,
      left,
      top,
      trimmed.width,
      trimmed.height,
      0,
      0,
      trimmed.width,
      trimmed.height,
    );
  return trimmed;
}

/** Every icon file, as base64 PNGs. */
export function renderIcons(art: HTMLCanvasElement, color: string): Record<string, string> {
  const icons: Record<string, string> = {};
  for (const [file, render] of Object.entries(RENDERS)) {
    const canvas = document.createElement('canvas');
    canvas.width = canvas.height = render.size;
    const g = canvas.getContext('2d')!;
    g.imageSmoothingQuality = 'high';
    if (render.background) {
      g.fillStyle = color;
      g.fillRect(0, 0, render.size, render.size);
    }
    const scale = (render.size * render.fill) / Math.max(art.width, art.height);
    const [dw, dh] = [art.width * scale, art.height * scale];
    g.drawImage(art, (render.size - dw) / 2, (render.size - dh) / 2, dw, dh);
    const url = canvas.toDataURL('image/png');
    icons[file] = url.slice(url.indexOf(',') + 1);
  }
  return icons;
}

/** Shows a saved brand on this page at once, as the server will serve it from now on. */
function applyToPage(before: BrandingInfo, brand: BrandingInfo): void {
  const custom = brand.color !== brand.defaults.color;
  document.querySelector('link[href^="branding/brand.css"]')?.remove();
  if (custom)
    document.head.append(
      h('link', { rel: 'stylesheet', href: `branding/brand.css?v=${brand.version}` }),
    );
  for (const name of document.querySelectorAll('.bar-brand-name')) name.textContent = brand.name;
  document.title = document.title.replace(before.name, brand.name);
  const meta = (name: string, value: string) =>
    document.querySelector<HTMLMetaElement>(`meta[name="${name}"]`)?.setAttribute('content', value);
  meta('application-name', brand.name);
  meta('apple-mobile-web-app-title', brand.shortName);
  meta('theme-color', brand.color);
  const icon = (file: string) =>
    brand.icon ? `branding/${file}?v=${brand.version}` : `icons/${file}`;
  for (const img of document.querySelectorAll<HTMLImageElement>('img.bar-brand-mark'))
    img.src = icon('icon-192.png');
  for (const link of document.querySelectorAll<HTMLLinkElement>(
    'link[rel="icon"], link[rel="apple-touch-icon"]',
  )) {
    const file = /([a-z0-9-]+\.png)/.exec(link.getAttribute('href') ?? '')?.[1];
    if (file) link.href = icon(file);
  }
}

export function brandingPanel(): { element: HTMLElement; load(): Promise<void> } {
  const body = h('div', { class: 'admin-cards' });
  let brand: BrandingInfo | undefined;

  function saved(next: BrandingInfo, message: string): void {
    if (brand) applyToPage(brand, next);
    brand = next;
    notify(message);
    render();
  }

  async function attempt(work: () => Promise<void>): Promise<void> {
    try {
      await work();
    } catch (error) {
      notify((error as Error).message, true);
    }
  }

  function nameCard(b: BrandingInfo): HTMLElement {
    const name = h('input', {
      class: 'admin-input',
      id: 'brand-name',
      maxlength: '60',
      value: b.name,
      placeholder: b.defaults.name,
    });
    const shortName = h('input', {
      class: 'admin-input',
      id: 'brand-short-name',
      maxlength: '20',
      value: b.shortName,
      placeholder: b.defaults.shortName,
    });
    const save = (n: string, s: string) =>
      attempt(async () => {
        const next = await api<BrandingInfo>('PUT', 'api/admin/branding', {
          name: n,
          shortName: s,
          color: b.color,
        });
        saved(next, 'Saved the name.');
      });
    const isDefault = b.name === b.defaults.name && b.shortName === b.defaults.shortName;
    return h(
      'section',
      { class: 'admin-card' },
      h('h2', {}, 'Name', isDefault ? h('span', { class: 'admin-tag', text: 'default' }) : null),
      h('p', {
        class: 'admin-intro',
        text: 'The name shows in the top bar and the browser tab. The short name is the installed app’s name under its icon, and what messages call it. Help pages keep their own wording.',
      }),
      h(
        'div',
        { class: 'admin-row' },
        h(
          'div',
          { class: 'admin-field' },
          h('label', { for: name.id, class: 'admin-label', text: 'Name' }),
          name,
        ),
        h(
          'div',
          { class: 'admin-field' },
          h('label', { for: shortName.id, class: 'admin-label', text: 'Short name' }),
          shortName,
        ),
        button('Save', () => void save(name.value, shortName.value)),
        isDefault
          ? null
          : button(
              `Use ${b.defaults.shortName}’s name`,
              () => void save('', ''),
              'admin-btn admin-btn-quiet',
            ),
      ),
    );
  }

  /** The icons drawn again, when the colour behind the installed app's icon changes. */
  async function redrawIcons(color: string): Promise<void> {
    const image = await loadImage(`branding/icon-512.png?v=${brand!.version}`);
    await api('PUT', 'api/admin/branding/icon', { icons: renderIcons(artwork(image), color) });
  }

  function colorCard(b: BrandingInfo): HTMLElement {
    const picker = h('input', {
      class: 'admin-color',
      id: 'brand-color',
      type: 'color',
      value: b.color,
    });
    const hex = h('input', {
      class: 'admin-input',
      id: 'brand-color-hex',
      value: b.color,
      maxlength: '7',
      pattern: '#[0-9a-fA-F]{6}',
      'aria-label': 'Brand colour as #rrggbb',
    });
    picker.addEventListener('input', () => (hex.value = picker.value));
    hex.addEventListener('input', () => {
      if (/^#[0-9a-f]{6}$/i.test(hex.value.trim())) picker.value = hex.value.trim().toLowerCase();
    });
    const save = (color: string) =>
      attempt(async () => {
        let next = await api<BrandingInfo>('PUT', 'api/admin/branding', {
          name: b.name,
          shortName: b.shortName,
          color,
        });
        if (next.icon && next.color !== b.color) {
          await redrawIcons(next.color);
          next = await api<BrandingInfo>('GET', 'api/admin/branding');
        }
        saved(next, 'Saved the brand colour.');
      });
    const isDefault = b.color === b.defaults.color;
    return h(
      'section',
      { class: 'admin-card' },
      h(
        'h2',
        {},
        'Brand colour',
        isDefault ? h('span', { class: 'admin-tag', text: 'default' }) : null,
      ),
      h('p', {
        class: 'admin-intro',
        text: 'The colour of the top bar, of the browser’s and installed app’s title bar, and of buttons and links. Shades for menus, Night Shift and dark mode are worked out from it, with text kept readable. Screens keep their own colours.',
      }),
      h(
        'div',
        { class: 'admin-row' },
        h(
          'div',
          { class: 'admin-field' },
          h('label', { for: picker.id, class: 'admin-label', text: 'Colour' }),
          h('div', { class: 'admin-row' }, picker, hex),
        ),
        button('Save', () => void save(hex.value.trim())),
        isDefault
          ? null
          : button(
              `Use ${b.defaults.shortName}’s colour`,
              () => void save(''),
              'admin-btn admin-btn-quiet',
            ),
      ),
    );
  }

  function iconCard(b: BrandingInfo): HTMLElement {
    const preview = h('img', {
      class: 'admin-brand-icon',
      src: b.icon ? `branding/icon-192.png?v=${b.version}` : 'icons/icon-192.png',
      alt: 'The icon in use',
      width: '64',
      height: '64',
    });
    const file = h('input', {
      class: 'admin-input',
      id: 'brand-icon-file',
      type: 'file',
      accept: 'image/png,image/svg+xml,image/webp,image/jpeg,image/gif',
    });
    file.addEventListener('change', () => {
      const picked = file.files?.[0];
      if (!picked) return;
      void attempt(async () => {
        let icons: Record<string, string>;
        try {
          icons = renderIcons(artwork(await loadImage(await dataUrl(picked))), b.color);
        } catch (error) {
          throw new Error(
            error instanceof Error && error.message.startsWith('The image')
              ? error.message
              : 'This browser can’t read that image. Try a PNG or SVG.',
            { cause: error },
          );
        } finally {
          // Picking the same file again (after fixing it) fires change again.
          file.value = '';
        }
        saved(
          await api<BrandingInfo>('PUT', 'api/admin/branding/icon', { icons }),
          'Saved the icon.',
        );
      });
    });
    return h(
      'section',
      { class: 'admin-card' },
      h('h2', {}, 'Icon', b.icon ? null : h('span', { class: 'admin-tag', text: 'default' })),
      h('p', {
        class: 'admin-intro',
        text: 'Shows in the top bar, the browser tab and as the installed app’s icon. Pick a square image, best a PNG or SVG with a transparent background, at least 512 pixels across. Transparent edges are trimmed; where a platform needs a background, the brand colour goes behind it.',
      }),
      h(
        'div',
        { class: 'admin-row' },
        preview,
        h(
          'div',
          { class: 'admin-field' },
          h('label', { for: file.id, class: 'admin-label', text: 'Image' }),
          file,
        ),
        b.icon
          ? button(
              `Use ${b.defaults.shortName}’s icon`,
              () =>
                void attempt(async () =>
                  saved(
                    await api<BrandingInfo>('DELETE', 'api/admin/branding/icon'),
                    'The icon is back to the default.',
                  ),
                ),
              'admin-btn admin-btn-quiet',
            )
          : null,
      ),
    );
  }

  function addressCard(b: BrandingInfo): HTMLElement {
    const url = h('input', {
      class: 'admin-input admin-field-wide',
      id: 'brand-address',
      type: 'url',
      value: b.address.current,
      placeholder: 'https://greenglow.example.com',
    });
    const save = (value: string) => {
      const target = value.trim().replace(/\/+$/, '') || b.address.configured;
      if (
        target !== b.address.current &&
        !confirm(
          `Change the address to ${target}?\n\n` +
            'Only browsers at this address (or one in the server’s configuration) can open sessions, share links point to it, ' +
            'and with https:// sign-in sends people back to it. Make sure it reaches this server first.',
        )
      )
        return;
      void attempt(async () => {
        const next = await api<BrandingInfo>('PUT', 'api/admin/address', {
          url: value.trim() || null,
        });
        brand = next;
        notify(`The address is ${next.address.current}.`);
        render();
      });
    };
    return h(
      'section',
      { class: 'admin-card' },
      h(
        'h2',
        {},
        'Address',
        b.address.saved === null
          ? h('span', { class: 'admin-tag', text: 'from configuration' })
          : null,
      ),
      h('p', {
        class: 'admin-intro',
        text: `The address people open ${b.shortName} at: http:// or https://, the host name, the port if it isn’t the default, and a path if it is under one (such as https://example.com/greenglow). Share links point to it, and only browsers at its host (or at an address in the server’s configuration) may open sessions. With https://, every request is treated as arriving there, as behind a TLS proxy. Under a path, the proxy passes the path on, or sends it as X-Forwarded-Prefix.`,
      }),
      h(
        'div',
        { class: 'admin-row' },
        h(
          'div',
          { class: 'admin-field admin-field-wide' },
          h('label', { for: url.id, class: 'admin-label', text: 'Address' }),
          url,
        ),
        button('Save', () => save(url.value)),
        b.address.saved === null
          ? null
          : button(
              `Use the configured address (${b.address.configured})`,
              () => save(''),
              'admin-btn admin-btn-quiet',
            ),
      ),
    );
  }

  function render(): void {
    if (!brand) return;
    body.replaceChildren(nameCard(brand), colorCard(brand), iconCard(brand), addressCard(brand));
  }

  async function load(): Promise<void> {
    brand = await api<BrandingInfo>('GET', 'api/admin/branding');
    render();
  }

  return {
    element: h(
      'section',
      { class: 'admin-panel' },
      h('p', {
        class: 'admin-intro',
        text: 'How the app presents itself to everyone, on every page and as an installed app. Pages already open show a change when they next load.',
      }),
      body,
    ),
    load,
  };
}
