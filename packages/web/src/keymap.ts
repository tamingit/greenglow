// The fixed Phase 0 key map: the ACS default 5250 keyboard, unchanged, so that no
// key changes meaning later. The editable map comes in Phase 1.
//
// From ACS (Edit > Preferences > Keyboard, 30 September 2026): Enter = Enter; Field Exit
// = Ctrl+Enter; New Line = Right Ctrl or Shift+Enter; Reset = Left Ctrl; Field+ and
// Field− = keypad + and −; Attention = Esc; System Request = Shift+Esc; Backspace deletes.
// The other keys follow the usual 5250 layout and are still to be checked against ACS.

import type { Key, KeyName } from './protocol/index.ts';

/** The parts of a KeyboardEvent the map reads, so it can be tested without a DOM. */
export interface KeyInput {
  key: string;
  code: string;
  shiftKey: boolean;
  ctrlKey: boolean;
  altKey: boolean;
  metaKey: boolean;
}

/**
 * Modifier keys that act on their own, when pressed and released with no other key in
 * between, as in ACS. Acting on release keeps Ctrl+Enter from also firing a Reset.
 */
export const SOLO_MODIFIERS: Readonly<Record<string, KeyName>> = {
  ControlLeft: 'Reset',
  ControlRight: 'NewLine',
};

const F_KEYS = Array.from({ length: 12 }, (_, i) => i + 1);

/** Keyed by modifiers (C = Ctrl, A = Alt, S = Shift) and KeyboardEvent.code. */
const MAP: Record<string, KeyName> = {
  Enter: 'Enter',
  NumpadEnter: 'Enter',
  'C-Enter': 'FieldExit',
  'C-NumpadEnter': 'FieldExit',
  'S-Enter': 'NewLine',
  'S-NumpadEnter': 'NewLine',
  NumpadAdd: 'FieldPlus',
  NumpadSubtract: 'FieldMinus',
  Escape: 'Attention',
  'S-Escape': 'SystemRequest',
  Tab: 'Tab',
  'S-Tab': 'Backtab',
  PageUp: 'RollDown',
  PageDown: 'RollUp',
  Home: 'Home',
  End: 'End',
  Insert: 'Insert',
  Delete: 'Delete',
  Backspace: 'Backspace',
  ArrowLeft: 'Left',
  ArrowRight: 'Right',
  ArrowUp: 'Up',
  ArrowDown: 'Down',
  // Still to be confirmed against ACS.
  'S-Insert': 'Dup',
  'C-End': 'EraseEof',
  'A-End': 'EraseInput',
  ...Object.fromEntries(F_KEYS.map((n) => [`F${n}`, `F${n}`])),
  ...Object.fromEntries(F_KEYS.map((n) => [`S-F${n}`, `F${n + 12}`])),
};

/** The 5250 key for a keydown, or undefined when the browser should keep it. */
export function mapKey(input: KeyInput): Key | undefined {
  if (input.metaKey) return undefined;
  const modifiers =
    (input.ctrlKey ? 'C-' : '') + (input.altKey ? 'A-' : '') + (input.shiftKey ? 'S-' : '');
  const name = MAP[modifiers + input.code];
  if (name) return { type: 'function', name };
  // Printable characters, shifted or not; Ctrl and Alt combinations stay with the browser.
  if (input.key.length === 1 && !input.ctrlKey && !input.altKey) {
    return { type: 'character', char: input.key };
  }
  return undefined;
}
