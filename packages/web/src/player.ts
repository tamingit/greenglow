// Playing a macro (hodmacro.ts) on a session, as Host-on-Demand plays it: find the macro's
// entry screen among what is showing, do that screen's actions, then wait for one of its next
// screens and go on, until an exit screen is done. The user asked for it (the ▶ button), so
// its keys go to the host as theirs; Esc stops it.
//
// A screen matches when every required description matches (and, when it has only optional
// ones, at least one of them); inverted ones must not match. Actions type text and host keys
// ('4[enter]'), ask for prompts (an encrypted one with a hidden box, never kept), pause, click
// and show messages. Others (extract, variables …) are skipped and reported.

import {
  hodText,
  type HodAction,
  type HodDescriptor,
  type HodMacro,
  type HodScreen,
} from './hodmacro.ts';
import type { Key, KeyName, ScreenSnapshot } from './protocol/index.ts';
import { screenView } from './recognize/view.ts';

/** HOD host key names (lower case, in brackets) to the 5250 keys the page sends. */
const KEYS: Record<string, KeyName> = {
  enter: 'Enter',
  pagedn: 'RollUp',
  pageup: 'RollDown',
  rollup: 'RollUp',
  rolldn: 'RollDown',
  help: 'Help',
  printhost: 'Print',
  clear: 'Clear',
  recbksp: 'RecordBackspace',
  fldext: 'FieldExit',
  'field+': 'FieldPlus',
  'field-': 'FieldMinus',
  dup: 'Dup',
  eraseeof: 'EraseEof',
  erinp: 'EraseInput',
  insert: 'Insert',
  delete: 'Delete',
  backspace: 'Backspace',
  home: 'Home',
  eof: 'End',
  tab: 'Tab',
  backtab: 'Backtab',
  newline: 'NewLine',
  left: 'Left',
  right: 'Right',
  up: 'Up',
  down: 'Down',
  reset: 'Reset',
  attn: 'Attention',
  sysreq: 'SystemRequest',
  ...Object.fromEntries(Array.from({ length: 24 }, (_, i) => [`pf${i + 1}`, `F${i + 1}`])),
};

/** Keys after which the host answers: playing waits for it before going on. */
const SENDS = new Set<KeyName>([
  'Enter',
  'RollUp',
  'RollDown',
  'Help',
  'Print',
  'Clear',
  'RecordBackspace',
  'Attention',
  'SystemRequest',
  ...Array.from({ length: 24 }, (_, i) => `F${i + 1}` as KeyName),
]);

export interface PlayerHost {
  /** The screen showing now. */
  screen(): ScreenSnapshot | undefined;
  press(key: Key): void;
  /** The next screen (or the one showing) that `match` accepts, once the host has answered. */
  waitFor(
    match: (screen: ScreenSnapshot) => boolean,
    ms: number,
  ): Promise<ScreenSnapshot | undefined>;
  /** After a key that goes to the host: its answer. */
  answer(ms: number): Promise<ScreenSnapshot | undefined>;
  ask(prompt: { name: string; default: string; secret: boolean }): Promise<string | undefined>;
  tell(title: string, message: string): Promise<void>;
  sleep(ms: number): Promise<void>;
  cancelled(): boolean;
}

export interface PlayResult {
  done: boolean;
  /** Why it stopped early, when it did. */
  stopped?: string;
  screens: number;
  /** Actions skipped because GreenGlow doesn't play them, by element name. */
  skipped: string[];
}

/** The text and host keys of an input action's value, as keys to press. */
export function keysOf(value: string): Key[] {
  const text = hodText(value);
  const keys: Key[] = [];
  for (let i = 0; i < text.length; i++) {
    const char = text[i];
    if (char !== '[') {
      keys.push({ type: 'character', char });
      continue;
    }
    const end = text.indexOf(']', i);
    const name = end > i ? KEYS[text.slice(i + 1, end).toLowerCase()] : undefined;
    if (!name) {
      // Not a host key: a bracket typed as text.
      keys.push({ type: 'character', char });
      continue;
    }
    keys.push({ type: 'function', name });
    i = end;
  }
  return keys;
}

/** Whether `snapshot` is `screen`, as its descriptions say. */
export function matches(screen: HodScreen, snapshot: ScreenSnapshot): boolean {
  const required = screen.description.filter((d) => !d.optional);
  const optional = screen.description.filter((d) => d.optional);
  if (!required.every((d) => describes(d, snapshot))) return false;
  return (
    required.length > 0 || optional.length === 0 || optional.some((d) => describes(d, snapshot))
  );
}

function describes(d: HodDescriptor, snapshot: ScreenSnapshot): boolean {
  return holds(d, snapshot) !== d.invert;
}

function holds(d: HodDescriptor, snapshot: ScreenSnapshot): boolean {
  switch (d.kind) {
    case 'oia':
      if (d.status === 'NOTINHIBITED') return snapshot.keyboard === 'unlocked';
      if (d.status === 'INHIBITED') return snapshot.keyboard !== 'unlocked';
      return true;
    case 'numfields':
      return snapshot.fields.length === d.number;
    case 'numinputfields':
      return snapshot.fields.filter((f) => !f.format.bypass).length === d.number;
    case 'cursor':
      return snapshot.cursor.row === d.row && snapshot.cursor.column === d.col;
    case 'string': {
      const wanted = hodText(d.value);
      const view = screenView(snapshot);
      const top = d.row > 0 ? d.row : 1;
      const bottom = d.erow > 0 ? Math.min(d.erow, snapshot.rows) : snapshot.rows;
      const left = d.col > 0 ? d.col : 1;
      const right = d.ecol > 0 ? Math.min(d.ecol, snapshot.columns) : snapshot.columns;
      for (let row = top; row <= bottom; row++) {
        const line = view.line(row).slice(left - 1, right);
        if (d.caseSense ? line.includes(wanted) : line.toLowerCase().includes(wanted.toLowerCase()))
          return true;
      }
      return false;
    }
    case 'other':
      // Not a check GreenGlow knows: it doesn't stand in the way.
      return true;
  }
}

/** Plays `macro` through `host`. */
export async function playMacro(macro: HodMacro, host: PlayerHost): Promise<PlayResult> {
  const result: PlayResult = { done: false, screens: 0, skipped: [] };
  const byName = new Map(macro.screens.map((s) => [s.name, s]));
  const entries = macro.screens.filter((s) => s.entry);
  let screen: HodScreen | undefined = entries.find((s) => {
    const now = host.screen();
    return now !== undefined && matches(s, now);
  });
  if (!screen) {
    const first = await host.waitFor((now) => entries.some((s) => matches(s, now)), 2000);
    screen = first && entries.find((s) => matches(s, first));
  }
  if (!screen)
    return { ...result, stopped: 'the screen showing isn’t the one the macro starts on' };
  while (screen) {
    result.screens++;
    for (const action of screen.actions) {
      if (host.cancelled()) return { ...result, stopped: 'you pressed Esc' };
      const why = await act(action, host, result);
      if (why) return { ...result, stopped: why };
    }
    if (screen.exit || screen.next.length === 0) return { ...result, done: true };
    const candidates: HodScreen[] = screen.next
      .map((name) => byName.get(name))
      .filter((s): s is HodScreen => s !== undefined);
    await host.sleep(macro.pauseTime);
    const ms = screen.nextTimeout || macro.timeout;
    const arrived = await host.waitFor((now) => candidates.some((s) => matches(s, now)), ms);
    if (host.cancelled()) return { ...result, stopped: 'you pressed Esc' };
    if (!arrived)
      return {
        ...result,
        stopped: `the next screen (${candidates.map((s) => s.name).join(' or ')}) didn’t come`,
      };
    screen = candidates.find((s) => matches(s, arrived));
  }
  return { ...result, done: true };
}

async function act(
  action: HodAction,
  host: PlayerHost,
  result: PlayResult,
): Promise<string | undefined> {
  switch (action.kind) {
    case 'input': {
      if (action.encrypted) return 'it holds a stored password';
      if (action.row > 0 && action.col > 0)
        host.press({ type: 'cursor', row: action.row, column: action.col });
      const keys = keysOf(action.value);
      for (const key of keys) {
        if (host.cancelled()) return 'you pressed Esc';
        host.press(key);
        if (key.type === 'function' && SENDS.has(key.name) && key !== keys.at(-1)) {
          // More keys follow: they go to the screen the host answers with.
          if (!(await host.answer(60000))) return 'the IBM i didn’t answer';
        }
      }
      return undefined;
    }
    case 'prompt': {
      const value = await host.ask({
        name: hodText(action.name) || `Row ${action.row}, column ${action.col}`,
        default: hodText(action.default),
        secret: action.encrypted,
      });
      if (value === undefined) return 'you cancelled a prompt';
      if (action.row > 0 && action.col > 0)
        host.press({ type: 'cursor', row: action.row, column: action.col });
      if (action.clearField) host.press({ type: 'function', name: 'EraseEof' });
      for (const char of value.slice(0, action.len || value.length))
        host.press({ type: 'character', char });
      return undefined;
    }
    case 'pause':
      await host.sleep(action.value);
      return undefined;
    case 'mouseclick':
      if (action.row > 0 && action.col > 0)
        host.press({ type: 'cursor', row: action.row, column: action.col });
      return undefined;
    case 'message':
      await host.tell(hodText(action.title) || 'Macro', hodText(action.value));
      return undefined;
    case 'other': {
      const name = /^<([\w:-]+)/.exec(action.xml)?.[1] ?? 'unknown';
      if (!result.skipped.includes(name)) result.skipped.push(name);
      return undefined;
    }
  }
}
