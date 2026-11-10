// Rectangular selection, as in ACS: a drag selects the block of cells between where it
// started and where the pointer is, not a run of wrapped text. The geometry and the text
// are pure functions; `Selection` holds the state. Nothing here sends anything to the host.

import type { Position, ScreenSnapshot } from './protocol/index.ts';
import type { Rect } from './decorations.ts';
import { operatorErrorText } from './messages.ts';
import { rowRuns, screenLines } from './text.ts';

/** The rectangle of cells (1-based) with two corners, whichever way the drag went. */
export function normalize(anchor: Position, focus: Position): Rect {
  const row = Math.min(anchor.row, focus.row);
  const column = Math.min(anchor.column, focus.column);
  return {
    row,
    column,
    rows: Math.max(anchor.row, focus.row) - row + 1,
    columns: Math.max(anchor.column, focus.column) - column + 1,
  };
}

/**
 * The text of a rectangle of the screen, row by row, as the renderer shows it: attribute
 * bytes and nulls are blanks, and so are nondisplay cells, whatever the buffer holds.
 * Trailing blanks are dropped from each line. The separate message line counts as one
 * more row below the screen.
 */
export function selectionText(snapshot: ScreenSnapshot, rect: Rect): string {
  const { columns } = snapshot;
  const lines = screenLines(snapshot);
  const error = snapshot.error;
  const errorText = error?.kind === 'operator' ? ` ${operatorErrorText(error.code)}` : undefined;
  const out: string[] = [];
  const last = Math.min(rect.row + rect.rows - 1, lines.length);
  for (let row = Math.max(1, rect.row); row <= last; row++) {
    const line = lines[row - 1];
    // The error line shows the operator error's text, so that is what is copied.
    const shown =
      errorText !== undefined && row === snapshot.errorRow
        ? errorText.padEnd(columns).slice(0, columns)
        : rowRuns(line.bytes, line.attributes)
            .map((run) => run.text)
            .join('');
    const start = Math.max(1, rect.column) - 1;
    out.push(shown.slice(start, rect.column + rect.columns - 1).replace(/ +$/, ''));
  }
  return out.join('\n');
}

export interface SelectionHost {
  /** Shows the rectangle, or nothing. */
  show(rect: Rect | undefined): void;
}

export class Selection {
  readonly #host: SelectionHost;
  #anchor: Position | undefined;
  #focus: Position | undefined;
  /** The size of the screen the selection is on: columns by lines. */
  #size = '';

  constructor(host: SelectionHost) {
    this.#host = host;
  }

  /** The selected rectangle, if there is one. */
  get rect(): Rect | undefined {
    return this.#anchor && this.#focus ? normalize(this.#anchor, this.#focus) : undefined;
  }

  /** Starts a selection at a cell, or moves its far corner. */
  drag(anchor: Position, focus: Position): void {
    this.#anchor = anchor;
    this.#focus = focus;
    this.#host.show(this.rect);
  }

  /** Moves the far corner to a cell; the start stays where it was. False if none. */
  extend(focus: Position): boolean {
    if (!this.#anchor) return false;
    this.#focus = focus;
    this.#host.show(this.rect);
    return true;
  }

  clear(): void {
    if (!this.#anchor) return;
    this.#anchor = this.#focus = undefined;
    this.#host.show(undefined);
  }

  /** A new screen. The selection stays, unless the screen is a different size. */
  see(snapshot: ScreenSnapshot): void {
    const size = `${snapshot.columns}x${snapshot.rows + (snapshot.messageLine ? 1 : 0)}`;
    if (size !== this.#size) this.clear();
    this.#size = size;
  }

  /** The selected text on a screen, if there is a selection. */
  text(snapshot: ScreenSnapshot): string | undefined {
    const rect = this.rect;
    return rect && selectionText(snapshot, rect);
  }
}

/**
 * Puts text on the clipboard. If the page may not use the clipboard API, a copy command
 * runs the page's `copy` event instead, whose handler sets the text.
 */
export async function copyText(text: string, terminal: HTMLElement): Promise<boolean> {
  try {
    await navigator.clipboard.writeText(text);
    return true;
  } catch {
    terminal.focus();
    return document.execCommand('copy');
  }
}
