// Looks a user can pick. Classic is always one of them and stays faithful to ACS; the
// others restyle the same screen. The choice is kept per browser.

import type { ScreenSnapshot } from '../protocol/index.ts';
import { decorate, hotspots } from '../decorate/index.ts';
import type { Decorations, Target } from '../decorations.ts';

export type LookId = 'classic' | 'greenglow' | 'night-shift';

export interface Look {
  readonly id: LookId;
  /** Shown in the picker. */
  readonly name: string;
  /** Classes on the terminal, after `terminal`. Night Shift reuses GreenGlow's rules. */
  readonly classes: readonly string[];
  /** What the look draws over the text. Classic has none, so its rendering never changes. */
  readonly decorate?: (snapshot: ScreenSnapshot) => Decorations;
  /** For a look that doesn't decorate: what a click acts on, with nothing drawn. */
  readonly hotspots?: (snapshot: ScreenSnapshot) => readonly Target[];
}

export const LOOKS: readonly Look[] = [
  // Classic draws nothing, but its function-key legends are hotspots, as ACS's are.
  { id: 'classic', name: 'Classic', classes: ['classic'], hotspots },
  { id: 'greenglow', name: 'GreenGlow', classes: ['greenglow'], decorate },
  { id: 'night-shift', name: 'Night Shift', classes: ['greenglow', 'night'], decorate },
];

/** The look a browser starts with. */
export const DEFAULT_LOOK: LookId = 'classic';

const STORAGE_KEY = 'greenglow.look';

export function lookById(id: string | null | undefined): Look | undefined {
  return LOOKS.find((look) => look.id === id);
}

/**
 * The look to show: `?look=` in the page address (for trying a look, not saved), then the
 * one saved in this browser, then the default. Unknown names are ignored.
 */
export function loadLook(storage: Pick<Storage, 'getItem'> | undefined, search: string): Look {
  let saved: string | null = null;
  try {
    saved = storage?.getItem(STORAGE_KEY) ?? null;
  } catch {
    // Storage blocked: fall back to the default.
  }
  const asked = new URLSearchParams(search).get('look');
  return lookById(asked) ?? lookById(saved) ?? (lookById(DEFAULT_LOOK) as Look);
}

/** Remembers the look in this browser, if it can. */
export function saveLook(storage: Pick<Storage, 'setItem'> | undefined, look: Look): void {
  try {
    storage?.setItem(STORAGE_KEY, look.id);
  } catch {
    // Storage blocked or full: the look still applies to this page.
  }
}
