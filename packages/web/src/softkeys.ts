// The on-screen keyboard of a touch screen such as an iPad's. The terminal isn't a text box,
// so tapping it brings up no keyboard. A tap on an input field focuses a hidden text box that
// sits on the cursor; what the on-screen keyboard types into it goes to the screen as the
// same keys typing sends, and the box is emptied again. Nothing else is sent. A hardware
// keyboard's keys still reach the terminal's own key handling, which they bubble up to.

import type { Key, Position, ScreenSnapshot } from './protocol/index.ts';

/**
 * Kept in the box so that Backspace has something to delete: an on-screen keyboard sends
 * nothing for Backspace in an empty box.
 */
export const SENTINEL = ' ';

/**
 * The keys for what the box holds after an edit: the text typed after the sentinel, with a
 * line break as Enter, or Backspace when the sentinel itself was deleted.
 */
export function softKeys(value: string): Key[] {
  if (!value.startsWith(SENTINEL)) {
    // The sentinel went: one Backspace, and whatever replaced it (a correction) typed after.
    return [{ type: 'function', name: 'Backspace' }, ...typed(value)];
  }
  return typed(value.slice(SENTINEL.length));
}

function typed(text: string): Key[] {
  return [...text].map((char) =>
    char === '\n' || char === '\r'
      ? { type: 'function', name: 'Enter' }
      : { type: 'character', char },
  );
}

/** Whether a position is in an input field (not a bypass field) of the screen. */
export function inInputField(snapshot: ScreenSnapshot, position: Position): boolean {
  const at = (position.row - 1) * snapshot.columns + position.column - 1;
  return snapshot.fields.some((field) => {
    if (field.format.bypass) return false;
    const start = (field.row - 1) * snapshot.columns + field.column - 1;
    return at >= start && at < start + field.length;
  });
}

export interface SoftKeyboardHost {
  readonly terminal: HTMLElement;
  /** Where the box goes: the screen's grid, whose cursor it follows. */
  readonly grid: HTMLElement;
  positionAt(clientX: number, clientY: number): Position | undefined;
  /** The engine's current screen. */
  live(): ScreenSnapshot | undefined;
  press(key: Key): void;
}

export class SoftKeyboard {
  readonly #input: HTMLTextAreaElement;
  readonly #host: SoftKeyboardHost;
  /** The last press on the terminal was a finger or a pen, not a mouse. */
  #touch = false;

  constructor(host: SoftKeyboardHost) {
    this.#host = host;
    const input = document.createElement('textarea');
    input.className = 'soft-input';
    input.rows = 1;
    input.spellcheck = false;
    input.tabIndex = -1;
    for (const [name, value] of [
      ['autocapitalize', 'off'],
      ['autocomplete', 'off'],
      ['autocorrect', 'off'],
      ['enterkeyhint', 'enter'],
      ['aria-label', 'Type into the field at the cursor'],
    ])
      input.setAttribute(name, value);
    host.grid.append(input);
    this.#input = input;
    this.#reset();

    host.terminal.addEventListener(
      'pointerdown',
      (event) => (this.#touch = event.pointerType !== 'mouse'),
      { capture: true },
    );
    // The click comes last in a tap, after the pointer's own handling moved the cursor.
    host.terminal.addEventListener('click', (event) => {
      if (!this.#touch) return;
      const position = host.positionAt(event.clientX, event.clientY);
      const live = host.live();
      if (position && live && inInputField(live, position)) this.focus();
      else if (this.open) host.terminal.focus();
    });
    input.addEventListener('input', (event) => {
      if (!event.isComposing) this.#take();
    });
    input.addEventListener('compositionend', () => this.#take());
  }

  /** Whether the on-screen keyboard is up: the box has the focus. */
  get open(): boolean {
    return document.activeElement === this.#input;
  }

  /** Puts the focus in the box, which brings up the on-screen keyboard. */
  focus(): void {
    this.#input.focus({ preventScroll: true });
    this.#reset();
  }

  /** Sends what was typed into the box, and empties it again. */
  #take(): void {
    const keys = softKeys(this.#input.value);
    this.#reset();
    for (const key of keys) this.#host.press(key);
  }

  #reset(): void {
    this.#input.value = SENTINEL;
    this.#input.setSelectionRange(SENTINEL.length, SENTINEL.length);
  }
}
