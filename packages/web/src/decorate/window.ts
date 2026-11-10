// The active window as a dialog: a card over its border, with the title on a bar along
// the top border row (or in bold on the first content row). The border characters are
// made transparent but keep their cells, and everything around the window is dimmed.

import type { Box, TextStyle } from '../decorations.ts';
import type { Recognized } from '../recognize/index.ts';
import type { ScreenView } from '../recognize/view.ts';

export function decorateWindow(
  view: ScreenView,
  recognized: Recognized,
): { boxes: Box[]; text: TextStyle[] } {
  const { window } = recognized;
  if (!window) return { boxes: [], text: [] };
  const { border, title, titleInBorder } = window;
  const top = border.row;
  const bottom = border.row + border.rows - 1;
  const left = border.column;
  const right = border.column + border.columns - 1;
  const text: TextStyle[] = [];
  const add = (className: string, row: number, from: number, to: number) => {
    if (to >= from) text.push({ span: { row, column: from, length: to - from + 1 }, className });
  };
  if (title && titleInBorder) {
    add('g-border', top, left, title.column - 1);
    add('g-border', top, title.column + title.length, right);
  } else {
    add('g-border', top, left, right);
  }
  for (let row = top + 1; row < bottom; row++) {
    add('g-border', row, left, left);
    add('g-border', row, right, right);
  }
  add('g-border', bottom, left, right);
  if (title) text.push({ span: title, className: titleInBorder ? 'g-window-title' : 'g-title' });
  // Everything around the window, the message line included.
  const lines = view.rows + (view.snapshot.messageLine ? 1 : 0);
  for (let row = 1; row <= lines; row++) {
    if (row < top || row > bottom) {
      add('g-inert', row, 1, view.columns);
    } else {
      add('g-inert', row, 1, left - 1);
      add('g-inert', row, right + 1, view.columns);
    }
  }
  const box: Box = {
    id: 'window',
    kind: 'window',
    rect: border,
    modifiers: [titleInBorder ? 'titled' : 'untitled'],
  };
  return { boxes: [box], text };
}
