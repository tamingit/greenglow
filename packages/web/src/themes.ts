// Themes: a user's own colours on top of a look (Classic, GreenGlow or Night Shift), kept per
// browser and per look, and saved to or loaded from a file to share. A theme only sets the
// look's colour tokens (--term-*, --gh-*) on the terminal; the look's structure and what it
// draws stay the same. Colours are #rrggbb only: a file can't carry anything else into the
// page.

import { button, h } from './admin/dom.ts';
import type { LookId } from './looks/look.ts';
import { closeOnBackdrop } from './notice.ts';
import './themes.css';

/** The colours a theme can set, in the order the dialog lists them. */
export const THEME_TOKENS = [
  { token: '--term-bg', label: 'Background' },
  { token: '--term-green', label: 'Green (normal text)' },
  { token: '--term-white', label: 'White (bright text)' },
  { token: '--term-red', label: 'Red' },
  { token: '--term-turquoise', label: 'Turquoise' },
  { token: '--term-yellow', label: 'Yellow' },
  { token: '--term-pink', label: 'Pink' },
  { token: '--term-blue', label: 'Blue' },
  { token: '--term-cursor', label: 'Cursor' },
  { token: '--term-rule-line', label: 'Rule line' },
  { token: '--term-status', label: 'Status line' },
  { token: '--gh-field', label: 'Input box', decorated: true },
  { token: '--gh-field-ink', label: 'Input box text', decorated: true },
  { token: '--gh-focus', label: 'Highlight', decorated: true },
] as const;

export type ThemeToken = (typeof THEME_TOKENS)[number]['token'];

export interface Theme {
  name: string;
  /** The look it is made for. */
  base: LookId;
  colors: Partial<Record<ThemeToken, string>>;
}

/** The file a theme is saved as. */
interface ThemeFile {
  format: 'greenglow-theme';
  version: 1;
  name: string;
  base: LookId;
  colors: Partial<Record<ThemeToken, string>>;
}

const COLOR = /^#[0-9a-f]{6}$/i;
const LOOK_IDS: readonly LookId[] = ['classic', 'greenglow', 'night-shift'];
const KEY = (look: LookId) => `greenglow.theme.${look}`;

/** A theme from untrusted JSON (a file or storage), or an error saying what is wrong. */
export function parseTheme(text: string): Theme | string {
  let data: unknown;
  try {
    data = JSON.parse(text);
  } catch {
    return 'This file isn’t a theme: it isn’t JSON.';
  }
  if (typeof data !== 'object' || data === null) return 'This file isn’t a theme.';
  const file = data as Partial<ThemeFile>;
  if (file.format !== 'greenglow-theme') return 'This file isn’t a GreenGlow theme.';
  if (file.version !== 1) return 'This theme is from a newer GreenGlow.';
  if (!LOOK_IDS.includes(file.base as LookId)) return 'This theme is for a look this page lacks.';
  const colors: Theme['colors'] = {};
  const given = (file.colors ?? {}) as Record<string, unknown>;
  for (const { token } of THEME_TOKENS) {
    const value = given[token];
    if (value === undefined) continue;
    if (typeof value !== 'string' || !COLOR.test(value)) return `${token} isn’t a #rrggbb colour.`;
    colors[token] = value.toLowerCase();
  }
  const name = typeof file.name === 'string' ? file.name.slice(0, 60).trim() : '';
  return { name: name || 'My theme', base: file.base as LookId, colors };
}

export function themeFile(theme: Theme): string {
  const file: ThemeFile = {
    format: 'greenglow-theme',
    version: 1,
    name: theme.name,
    base: theme.base,
    colors: theme.colors,
  };
  return JSON.stringify(file, null, 2) + '\n';
}

export function loadTheme(storage: Storage | undefined, look: LookId): Theme | undefined {
  try {
    const saved = storage?.getItem(KEY(look));
    if (!saved) return undefined;
    const theme = parseTheme(saved);
    return typeof theme === 'string' || theme.base !== look ? undefined : theme;
  } catch {
    return undefined;
  }
}

export function saveTheme(
  storage: Storage | undefined,
  look: LookId,
  theme: Theme | undefined,
): void {
  try {
    if (theme && Object.keys(theme.colors).length > 0)
      storage?.setItem(KEY(look), themeFile(theme));
    else storage?.removeItem(KEY(look));
  } catch {
    // Storage blocked or full: the theme still applies to this page.
  }
}

/** Sets a theme's colours on the terminal, clearing any set before. */
export function applyTheme(terminal: HTMLElement, theme: Theme | undefined): void {
  for (const { token } of THEME_TOKENS) terminal.style.removeProperty(token);
  for (const [token, value] of Object.entries(theme?.colors ?? {}))
    if (COLOR.test(value)) terminal.style.setProperty(token, value);
}

/** A colour as #rrggbb, whatever CSS form it has (the look's own value, read from the page). */
function asHex(value: string): string {
  const context = document.createElement('canvas').getContext('2d');
  if (!context) return '#000000';
  context.fillStyle = '#000000';
  context.fillStyle = value.trim() || '#000000';
  const normal = context.fillStyle;
  if (COLOR.test(normal)) return normal;
  const rgb = /rgba?\((\d+),\s*(\d+),\s*(\d+)/.exec(normal);
  return rgb
    ? `#${rgb
        .slice(1, 4)
        .map((n) => Number(n).toString(16).padStart(2, '0'))
        .join('')}`
    : '#000000';
}

export interface ThemeDialogHost {
  terminal: HTMLElement;
  look: { id: LookId; name: string; decorated: boolean };
  /** The theme changed (edited, reset or imported): keep it. */
  changed(theme: Theme | undefined): void;
  /** An imported theme is for another look: switch to it first. */
  switchLook(look: LookId): void;
  closed(): void;
}

/** Customize colours: the current look's colours, each editable, with Reset, Save and Load. */
export function openThemeDialog(host: ThemeDialogHost, current: Theme | undefined): void {
  const { terminal, look } = host;
  let theme: Theme = current ?? { name: 'My theme', base: look.id, colors: {} };
  const status = h('p', { class: 'theme-status', role: 'status' });
  const name = h('input', {
    class: 'theme-name',
    type: 'text',
    value: theme.name,
    maxlength: '60',
    'aria-label': 'Theme name',
  });
  name.addEventListener('input', () => {
    theme = { ...theme, name: name.value.trim() || 'My theme' };
    host.changed(theme);
  });
  const list = h('div', { class: 'theme-colors' });

  function paint(): void {
    applyTheme(terminal, undefined);
    const styles = getComputedStyle(terminal);
    const defaults = new Map(
      THEME_TOKENS.map(({ token }) => [
        token,
        asHex(styles.getPropertyValue(token) || (token === '--term-rule-line' ? '#ff3b3b' : '')),
      ]),
    );
    applyTheme(terminal, theme);
    list.replaceChildren(
      ...THEME_TOKENS.filter((t) => look.decorated || !('decorated' in t)).map(
        ({ token, label }) => {
          const id = `theme${token}`;
          const input = h('input', {
            id,
            type: 'color',
            value: theme.colors[token] ?? defaults.get(token) ?? '#000000',
          });
          input.addEventListener('input', () => {
            theme = { ...theme, colors: { ...theme.colors, [token]: input.value.toLowerCase() } };
            applyTheme(terminal, theme);
            host.changed(theme);
          });
          return h(
            'div',
            { class: 'theme-color', 'data-changed': token in theme.colors },
            h('label', { for: id, text: label }),
            input,
          );
        },
      ),
    );
  }

  const file = h('input', { type: 'file', accept: '.json,application/json', hidden: true });
  file.addEventListener('change', () => {
    const chosen = file.files?.[0];
    file.value = '';
    if (!chosen) return;
    void chosen.text().then((text) => {
      const loaded = parseTheme(text);
      if (typeof loaded === 'string') {
        status.textContent = loaded;
        return;
      }
      if (loaded.base !== look.id) {
        // The look changes, so the dialog opens again for it.
        dialog.close();
        host.switchLook(loaded.base);
        host.changed(loaded);
        return;
      }
      theme = loaded;
      name.value = theme.name;
      host.changed(theme);
      paint();
      status.textContent = `Loaded ${theme.name}.`;
    });
  });

  const save = button(
    'Save to a file',
    () => {
      const link = h('a', {
        href: URL.createObjectURL(new Blob([themeFile(theme)], { type: 'application/json' })),
        download: `${theme.name.replace(/[^\w -]+/g, '').trim() || 'theme'}.greenglow-theme.json`,
      });
      link.click();
      setTimeout(() => URL.revokeObjectURL(link.href), 1000);
      status.textContent = 'Saved. Load the file on another browser to use the same colours.';
    },
    'theme-btn',
  );
  const load = button('Load from a file…', () => file.click(), 'theme-btn');
  const reset = button(
    `Use ${look.name}’s colours`,
    () => {
      theme = { ...theme, colors: {} };
      host.changed(undefined);
      paint();
      status.textContent = `Back to ${look.name}’s own colours.`;
    },
    'theme-btn',
  );
  const done = button('Done', () => dialog.close(), 'theme-btn theme-btn-primary');
  const dialog = h(
    'dialog',
    { class: 'theme-dialog', 'aria-labelledby': 'theme-title' },
    h('h2', { id: 'theme-title', text: `Customize ${look.name}` }),
    h('p', {
      class: 'theme-intro',
      text: 'Your colours apply to this look in this browser. Save them to a file to use them elsewhere or share them.',
    }),
    h('label', { class: 'theme-name-row' }, h('span', { text: 'Name' }), name),
    list,
    status,
    h('div', { class: 'theme-row' }, reset, load, save, done, file),
  );
  dialog.addEventListener('close', () => {
    dialog.remove();
    host.closed();
  });
  // Keys typed here belong to the dialog, not the terminal under it.
  dialog.addEventListener('keydown', (event) => event.stopPropagation());
  closeOnBackdrop(dialog);
  document.body.append(dialog);
  paint();
  dialog.showModal();
}
