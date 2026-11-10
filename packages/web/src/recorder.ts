// Recording a macro: the keys the user presses on a session, screen by screen, as a
// Host-on-Demand macro (hodmacro.ts) that ACS can play too. Each key that sends the screen to
// the host (Enter, a function key, Page Up and so on) ends a screen: the screen the keys were
// pressed on is described by its title, the white (high-intensity) text on its first line, and
// its action types what was pressed, host keys in brackets ('4[enter]'; a bracket the user
// types is kept as typed). Its field counts are recorded too, but optional, as ACS records
// them. A first screen with no white title can't be told apart, so it is described only by the
// keyboard being free, as ACS records an entry screen: the macro starts on whatever shows.
// Stopping adds the screen showing then as the macro's last.
//
// What is typed into a nondisplay field (a password) is never recorded: the macro gets a
// prompt there instead, which asks for it when the macro is played.

import { hodString, type HodAction, type HodMacro, type HodScreen } from './hodmacro.ts';
import {
  FUNCTION_KEYS,
  displayCharacter,
  isAttribute,
  isNondisplay,
  type Key,
  type KeyName,
  type ScreenSnapshot,
} from './protocol/index.ts';

/** HOD's names for the 5250's keys, as they go in an input action's text. */
const HOD_KEYS: Partial<Record<KeyName, string>> = {
  Enter: '[enter]',
  RollUp: '[pagedn]',
  RollDown: '[pageup]',
  Help: '[help]',
  Print: '[printhost]',
  Clear: '[clear]',
  RecordBackspace: '[recbksp]',
  FieldExit: '[fldext]',
  FieldPlus: '[field+]',
  FieldMinus: '[field-]',
  Dup: '[dup]',
  EraseEof: '[eraseeof]',
  EraseInput: '[erinp]',
  Insert: '[insert]',
  Delete: '[delete]',
  Backspace: '[backspace]',
  Home: '[home]',
  End: '[eof]',
  Tab: '[tab]',
  Backtab: '[backtab]',
  NewLine: '[newline]',
  Left: '[left]',
  Right: '[right]',
  Up: '[up]',
  Down: '[down]',
  Reset: '[reset]',
  Attention: '[attn]',
  SystemRequest: '[sysreq]',
  ...Object.fromEntries(Array.from({ length: 24 }, (_, i) => [`F${i + 1}`, `[pf${i + 1}]`])),
};

/** Keys after which the host answers with the next screen: the AID keys and the signals. */
const SENDS = new Set<KeyName>([
  ...FUNCTION_KEYS,
  'Enter',
  'RollUp',
  'RollDown',
  'Help',
  'Print',
  'Clear',
  'RecordBackspace',
  'Attention',
  'SystemRequest',
]);

interface Pending {
  snapshot: ScreenSnapshot;
  actions: HodAction[];
  /** The text of the input action being built, if the last action is one. */
  text: string | undefined;
  /** Where that input action starts, when a click put the cursor there; else 0, 0. */
  at: { row: number; col: number };
}

export class MacroRecorder {
  readonly name: string;
  readonly #screens: HodScreen[] = [];
  #pending: Pending | undefined;

  constructor(name: string) {
    this.name = name;
  }

  /** How many screens are recorded so far, counting the one being typed on. */
  get screens(): number {
    return this.#screens.length + (this.#pending ? 1 : 0);
  }

  /** A key the user pressed on `screen` (the screen before the key). */
  key(key: Key, screen: ScreenSnapshot): void {
    const pending = this.#on(screen);
    if (key.type === 'cursor') {
      this.#close(pending);
      pending.at = { row: key.row, col: key.column };
      return;
    }
    if (key.type === 'character') {
      const field = fieldAt(screen, screen.cursor.row, screen.cursor.column);
      if (field && isNondisplay(field.attribute)) {
        this.#prompt(pending, field);
        return;
      }
      this.#type(pending, key.char);
      return;
    }
    const hod = HOD_KEYS[key.name];
    if (hod) this.#type(pending, hod);
    if (SENDS.has(key.name)) this.#endScreen();
  }

  /** Text pasted on `screen`: typed as the user's keys, a tab moving to the next field. */
  paste(text: string, screen: ScreenSnapshot): void {
    const pending = this.#on(screen);
    const field = fieldAt(screen, screen.cursor.row, screen.cursor.column);
    if (field && isNondisplay(field.attribute)) {
      this.#prompt(pending, field);
      return;
    }
    this.#type(pending, text.replace(/\r?\n/g, '[newline]').replace(/\t/g, '[tab]'));
  }

  /** The macro: the screens recorded, then `last`, the screen showing now, to end on. */
  finish(last: ScreenSnapshot | undefined): HodMacro {
    if (this.#pending && this.#pending.actions.length + (this.#pending.text ? 1 : 0) > 0)
      this.#endScreen();
    const screens = [...this.#screens];
    if (last) screens.push(screenOf(last, [], screens.length + 1));
    screens.forEach((screen, i) => {
      screen.entry = i === 0;
      screen.exit = i === screens.length - 1;
      screen.next = i < screens.length - 1 ? [screens[i + 1].name] : [];
    });
    // A first screen with no title to know it by: it starts wherever the user is.
    const first = screens[0];
    if (first && !first.description.some((d) => d.kind === 'string'))
      first.description = first.description.filter((d) => d.kind === 'oia');
    return {
      name: this.name,
      description: '',
      timeout: 60000,
      pauseTime: 300,
      attributes: {
        promptall: 'true',
        blockinput: 'false',
        author: '',
        creationdate: new Date().toISOString(),
        supressclearevents: 'false',
        usevars: 'false',
        ignorepauseforenhancedtn: 'true',
        delayifnotenhancedtn: '0',
        ignorepausetimeforenhancedtn: 'true',
        continueontimeout: 'false',
      },
      screens,
    };
  }

  #on(screen: ScreenSnapshot): Pending {
    this.#pending ??= {
      snapshot: screen,
      actions: [],
      text: undefined,
      at: { row: 0, col: 0 },
    };
    return this.#pending;
  }

  #type(pending: Pending, text: string): void {
    pending.text = (pending.text ?? '') + text;
  }

  #close(pending: Pending): void {
    if (pending.text === undefined) return;
    pending.actions.push({
      kind: 'input',
      value: hodString(pending.text),
      row: pending.at.row,
      col: pending.at.col,
      moveCursor: true,
      xlateHostKeys: true,
      encrypted: false,
    });
    pending.text = undefined;
    pending.at = { row: 0, col: 0 };
  }

  /** A nondisplay field: a prompt asks for its value at play time; what was typed is dropped. */
  #prompt(pending: Pending, field: { row: number; column: number; length: number }): void {
    const last = pending.actions.at(-1);
    if (
      pending.text === undefined &&
      last?.kind === 'prompt' &&
      last.row === field.row &&
      last.col === field.column
    )
      return;
    this.#close(pending);
    pending.actions.push({
      kind: 'prompt',
      name: hodString(`Value for row ${field.row}, column ${field.column}`),
      row: field.row,
      col: field.column,
      len: field.length,
      default: '',
      encrypted: true,
      clearField: true,
    });
  }

  #endScreen(): void {
    const pending = this.#pending;
    if (!pending) return;
    this.#close(pending);
    this.#screens.push(screenOf(pending.snapshot, pending.actions, this.#screens.length + 1));
    this.#pending = undefined;
  }
}

/**
 * A screen's title: the longest run of white (high-intensity) text on its first line, words
 * separated by single spaces; empty when the line has none. A program or format ID in normal
 * colour beside it, and a date or time, don't count.
 */
export function whiteTitle(snapshot: ScreenSnapshot): string {
  const runs: string[] = [];
  let run = '';
  for (let column = 0; column <= snapshot.columns; column++) {
    const byte = snapshot.buffer[column];
    const white =
      column < snapshot.columns &&
      !isAttribute(byte) &&
      WHITE.has(snapshot.attributes[column]) &&
      displayCharacter(byte) !== ' ';
    if (white) {
      run += displayCharacter(byte);
      continue;
    }
    // A single space inside white text joins its words.
    const space =
      column < snapshot.columns &&
      displayCharacter(byte) === ' ' &&
      WHITE.has(snapshot.attributes[column]);
    if (space && run && !run.endsWith(' ')) {
      run += ' ';
      continue;
    }
    if (run.trim()) runs.push(run.trim());
    run = '';
  }
  return runs.filter((r) => /[A-Za-z]/.test(r)).reduce((a, b) => (b.length > a.length ? b : a), '');
}

/** White: high intensity, also with reverse image or underscore (not nondisplay). */
const WHITE = new Set([0x22, 0x23, 0x26]);

/**
 * A screen as HOD recognises it: the keyboard free, its white title on the first line, and its
 * field counts (optional, as ACS records them).
 */
function screenOf(snapshot: ScreenSnapshot, actions: HodAction[], n: number): HodScreen {
  const title = whiteTitle(snapshot);
  const inputs = snapshot.fields.filter((f) => !f.format.bypass).length;
  return {
    name: `Screen${n}`,
    entry: false,
    exit: false,
    transient: false,
    description: [
      { kind: 'oia', status: 'NOTINHIBITED', optional: false, invert: false },
      { kind: 'numfields', number: snapshot.fields.length, optional: true, invert: false },
      { kind: 'numinputfields', number: inputs, optional: true, invert: false },
      ...(title
        ? [
            {
              kind: 'string' as const,
              value: hodString(title),
              row: 1,
              col: 1,
              erow: 1,
              ecol: snapshot.columns,
              caseSense: false,
              optional: false,
              invert: false,
            },
          ]
        : []),
    ],
    actions,
    next: [],
    nextTimeout: 0,
  };
}

/** The field holding a position, if any (a field may wrap onto the next rows). */
function fieldAt(snapshot: ScreenSnapshot, row: number, column: number) {
  const at = (row - 1) * snapshot.columns + column - 1;
  return snapshot.fields.find((f) => {
    const start = (f.row - 1) * snapshot.columns + f.column - 1;
    return at >= start && at < start + f.length;
  });
}
