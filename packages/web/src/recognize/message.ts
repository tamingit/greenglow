// The message on the error line: what the host wrote there (Write Error Code, or a
// program's message line), or an operator error, whose text the renderer draws. Inside a
// window, only the window's content columns are read, so its border is never a message.

import type { Span } from '../decorations.ts';
import { operatorErrorText } from '../messages.ts';
import { isHighIntensity, isRed, isReverse, type ScreenView } from './view.ts';

export interface Message {
  /** A screen row, or `rows + 1` for the separate message line. */
  readonly row: number;
  /** Where the text starts and how long it is. */
  readonly column: number;
  readonly length: number;
  readonly kind: 'error' | 'info';
  /** The text ends in " +": the host has more messages. */
  readonly more: boolean;
  /** What the banner covers: the whole row, or a window's content columns. */
  readonly band: Span;
}

/** IBM's copyright line sits on the error row of several system screens. */
const COPYRIGHT = /\(C\) COPYRIGHT/i;
/**
 * Function-key items on the error row (some menus put their legend on row 24) are not a
 * message, even when the legend recognizer didn't take the whole row.
 */
const LEGEND = /(?:^|\s)F(?:[1-9]|1\d|2[0-4])=\S/;

export function recognizeMessage(view: ScreenView, claimed: Set<number>): Message | undefined {
  const { snapshot, rows, columns, bounds } = view;
  const row = snapshot.errorRow;
  if (row > rows && !snapshot.messageLine) return undefined;
  if (claimed.has(row) && snapshot.error?.kind !== 'operator') return undefined;
  if (snapshot.error?.kind === 'operator') {
    // The renderer writes " <text>" over the whole error row, window or not.
    const length = operatorErrorText(snapshot.error.code).length;
    const band = { row, column: 1, length: columns };
    return { row, column: 2, length, kind: 'error', more: false, band };
  }
  const text = view.line(row);
  const start = text.search(/\S/);
  if (start < 0 || COPYRIGHT.test(text) || LEGEND.test(text)) return undefined;
  const trimmed = text.trimEnd();
  const column = start + 1;
  const attribute = view.attribute(row, column);
  const host = snapshot.error?.kind === 'host';
  // Without the host's error state, only text that looks like a message counts: near the
  // left edge (of the screen or the window), in white or red.
  const nearEdge = column - bounds.column < 3;
  if (!host && !(nearEdge && (isHighIntensity(attribute) || isRed(attribute)))) {
    return undefined;
  }
  const error = host || isRed(attribute) || hasReversedInput(view);
  const band =
    view.scope === 'window'
      ? { row, column: bounds.column, length: bounds.columns }
      : { row, column: 1, length: columns };
  return {
    row,
    column,
    length: trimmed.length - start,
    kind: error ? 'error' : 'info',
    more: / \+$/.test(trimmed),
    band,
  };
}

/**
 * The host marks fields in error with reverse image. A screen that shows most of its
 * inputs in reverse is styled that way, so that's no sign of an error.
 */
function hasReversedInput(view: ScreenView): boolean {
  const reversed = view.inputs.filter((field) =>
    isReverse(view.attribute(field.row, field.column)),
  ).length;
  return reversed > 0 && reversed * 2 <= Math.max(view.inputs.length, 2);
}
