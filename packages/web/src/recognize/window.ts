// Windows the host draws on the screen (DDS WINDOW). With the default border:
//
//   ¦..........Title..........¦      a dot run with an attribute byte just before it
//   ¦:¦  content            ¦:¦      ":" at both ends of every row below
//   ¦:.......................:¦      a dot row at least two rows lower
//
// With WDWBORDER((*DSPATR RI) (*CHAR '        ')), the same cells hold reverse-image
// blanks instead: a bar along the top and bottom, and one reverse cell on each side, each
// between attribute bytes. Either way the content is two columns in from each border (the
// border cell and its attribute byte). Borders of plain blanks, which show nothing, are
// not recognized.

import type { Rect, Span } from '../decorations.ts';
import { contains, isReverse, type ScreenView } from './view.ts';

export interface ScreenWindow {
  /** From the top border row to the bottom one, and the border columns L to R. */
  readonly border: Rect;
  /** The window's own text: columns L + 2 to R − 2 on the rows between the borders. */
  readonly content: Rect;
  /** The title, in the top border or centred on the first content row. */
  readonly title: Span | undefined;
  readonly titleInBorder: boolean;
}

/** The top border: at least six dots, or dots around a title. */
const TOP = /^(\.{2,})(?:((?:[^.]|\.(?!\.))+?)(\.{2,}))?/;
/** The fewest cells in a top border. */
const MIN_WIDTH = 6;

/** Every window drawn with dots and colons or with reverse-image bars, in screen order. */
export function recognizeWindows(view: ScreenView): ScreenWindow[] {
  const windows: ScreenWindow[] = [];
  for (let row = 1; row <= view.rows - 2; row++) {
    for (let left = 2; left <= view.columns; left++) {
      if (!view.isAttributeCell(row, left - 1)) continue;
      const window = dotWindowAt(view, row, left) ?? barWindowAt(view, row, left);
      if (!window) continue;
      windows.push(window);
      left += window.border.columns - 1;
    }
  }
  return windows;
}

function dotWindowAt(view: ScreenView, top: number, left: number): ScreenWindow | undefined {
  const match = TOP.exec(view.line(top).slice(left - 1));
  if (!match) return undefined;
  const [run, before, title = '', after = ''] = match;
  if (before.length + after.length < MIN_WIDTH) return undefined;
  const right = left + run.length - 1;
  for (let row = top + 1; row <= view.rows; row++) {
    const line = view.line(row);
    const inside = line.slice(left, right - 1);
    const ends = line[left - 1] + line[right - 1];
    if (row >= top + 2 && /^\.+$/.test(inside) && /^[:.]{2}$/.test(ends)) {
      const text = title.trim();
      if (text === '') return framed(view, top, row, left, right);
      const window = framed(view, top, row, left, right);
      const column = left + before.length + title.indexOf(text);
      return (
        window && {
          ...window,
          title: { row: top, column, length: text.length },
          titleInBorder: true,
        }
      );
    }
    if (ends !== '::') return undefined;
  }
  return undefined;
}

function barWindowAt(view: ScreenView, top: number, left: number): ScreenWindow | undefined {
  const right = barEnd(view, top, left);
  if (right === undefined || right - left + 1 < MIN_WIDTH) return undefined;
  for (let row = top + 1; row <= view.rows; row++) {
    if (
      row >= top + 2 &&
      view.isAttributeCell(row, left - 1) &&
      barEnd(view, row, left) === right
    ) {
      return framed(view, top, row, left, right);
    }
    if (!barSide(view, row, left) || !barSide(view, row, right)) return undefined;
  }
  return undefined;
}

/** A reverse-image blank: what a bar border is made of. */
function isBarCell(view: ScreenView, row: number, column: number): boolean {
  return (
    view.line(row)[column - 1] === ' ' &&
    !view.isAttributeCell(row, column) &&
    isReverse(view.attribute(row, column))
  );
}

/** The last column of a bar from `left`, which an attribute byte or the row's end closes. */
function barEnd(view: ScreenView, row: number, left: number): number | undefined {
  let column = left;
  while (column <= view.columns && isBarCell(view, row, column)) column++;
  if (column === left) return undefined;
  if (column <= view.columns && !view.isAttributeCell(row, column)) return undefined;
  return column - 1;
}

/** One side of a bar window's row: a bar cell between attribute bytes. */
function barSide(view: ScreenView, row: number, column: number): boolean {
  return (
    view.isAttributeCell(row, column - 1) &&
    isBarCell(view, row, column) &&
    (column === view.columns || view.isAttributeCell(row, column + 1))
  );
}

/** The window between border rows `top` and `bottom` and border columns `left` and `right`. */
function framed(
  view: ScreenView,
  top: number,
  bottom: number,
  left: number,
  right: number,
): ScreenWindow | undefined {
  const content = {
    row: top + 1,
    column: left + 2,
    rows: bottom - top - 1,
    columns: right - left - 3,
  };
  if (content.columns < 1) return undefined;
  return {
    border: { row: top, column: left, rows: bottom - top + 1, columns: right - left + 1 },
    content,
    title: centredTitle(view, content),
    titleInBorder: false,
  };
}

/** A first content row holding one centred, lettered phrase and no input field. */
function centredTitle(view: ScreenView, content: Rect): Span | undefined {
  const { row, column, columns } = content;
  const text = view.line(row).slice(column - 1, column - 1 + columns);
  const words = text.trim();
  if (!/[A-Za-z]/.test(words) || / {2}/.test(words)) return undefined;
  const start = column + text.indexOf(words);
  const centre = column + (columns - 1) / 2;
  if (Math.abs(start + (words.length - 1) / 2 - centre) > columns * 0.15) return undefined;
  const span = { row, column: start, length: words.length };
  if (view.inputs.some((field) => field.row === row && overlaps(field, span))) return undefined;
  return span;
}

function overlaps(field: { column: number; length: number }, span: Span): boolean {
  return field.column < span.column + span.length && span.column < field.column + field.length;
}

/**
 * The window the operator is working in: the one holding the cursor, else the last one
 * with an input field, else the last one. Only one window is live; any others are part of
 * the screen behind it.
 */
export function activeWindow(
  view: ScreenView,
  windows: readonly ScreenWindow[],
): ScreenWindow | undefined {
  const { cursor } = view.snapshot;
  return (
    windows.find((w) => contains(w.border, cursor.row, cursor.column)) ??
    windows.findLast((w) =>
      view.inputs.some((field) => contains(w.content, field.row, field.column)),
    ) ??
    windows.at(-1)
  );
}
