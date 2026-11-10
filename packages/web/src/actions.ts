// What a click sends: exactly the keys a user would type, checked against the live screen
// (the painted one can be a frame old) and never typed ahead.

import type { Key, ScreenSnapshot } from './protocol/index.ts';
import type { Action } from './decorations.ts';
import { screenView } from './recognize/view.ts';

/** The keys for an action on the live screen, or undefined if it no longer applies. */
export function keysFor(action: Action, live: ScreenSnapshot): Key[] | undefined {
  switch (action.type) {
    case 'key': {
      const { span, text } = action.guard;
      const shown = screenView(live)
        .line(span.row)
        .slice(span.column - 1, span.column - 1 + span.length);
      return shown === text ? [{ type: 'function', name: action.key }] : undefined;
    }
    case 'copy':
      return [];
  }
}

export interface KeyContext {
  /** The engine's current screen. */
  live(): ScreenSnapshot | undefined;
  press(key: Key): void;
}

export type RunResult = 'done' | 'stopped' | 'refused';

/**
 * Presses keys for a click. Unlike typed keys, a click is never typed ahead: it is
 * refused while the keyboard is locked (or after Help in an error), and stops if the
 * keyboard locks partway, so nothing aimed at this screen lands on the next one.
 *
 * In an error state, Reset goes first. In ACS any key clears an error and then does its
 * job; Reset only clears it, so the keys after it go out as they would there. If the host
 * locked the keyboard during the error, the click stops instead of typing ahead.
 */
export function runKeys(keys: readonly Key[], context: KeyContext): RunResult {
  const live = context.live();
  if (!live || live.keyboard === 'locked' || live.error?.help) return 'refused';
  if (live.keyboard === 'error') {
    context.press({ type: 'function', name: 'Reset' });
    if (context.live()?.keyboard !== 'unlocked') return 'stopped';
  }
  for (const [i, key] of keys.entries()) {
    if (i > 0 && context.live()?.keyboard !== 'unlocked') return 'stopped';
    context.press(key);
  }
  return 'done';
}
