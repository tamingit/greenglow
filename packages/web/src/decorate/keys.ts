// Function-key legend items as pills: "F3=Exit" keeps every character in its cell, with
// a keycap behind "F3", the "=" made transparent, and a click target that presses F3.
// Behind an active window the pills are drawn inert, with no targets.

import type { AidKeyName } from '../protocol/index.ts';
import type { Box, Target, TextStyle } from '../decorations.ts';
import type { Scope } from '../recognize/index.ts';

export function decorateKeys(scope: Scope): { boxes: Box[]; text: TextStyle[]; targets: Target[] } {
  const { view, inert } = scope;
  const modifiers = inert ? ['inert'] : [];
  const boxes: Box[] = [];
  const text: TextStyle[] = [];
  const targets: Target[] = [];
  for (const legend of scope.keys) {
    for (const item of legend.items) {
      const { span } = item;
      const id = `pill:${span.row}:${span.column}`;
      boxes.push({
        id,
        kind: 'pill',
        rect: { row: span.row, column: span.column, rows: 1, columns: span.length },
        modifiers,
      });
      if (item.label === '') {
        // A bare roll key ("Rolldown"): the whole word is the pill.
        text.push({ span, className: 'g-pill' });
      } else {
        const labelColumn = span.column + span.length - item.label.length;
        boxes.push({
          id: `keycap:${span.row}:${span.column}`,
          kind: 'keycap',
          rect: { row: span.row, column: span.column, rows: 1, columns: item.codeLength },
          modifiers,
        });
        const code = { row: span.row, column: span.column, length: item.codeLength };
        const gap = labelColumn - span.column - item.codeLength;
        text.push({ span: code, className: 'g-keycap' });
        if (gap > 0) {
          const sep = { row: span.row, column: span.column + item.codeLength, length: gap };
          text.push({ span: sep, className: 'g-sep' });
        }
        text.push({
          span: { row: span.row, column: labelColumn, length: item.label.length },
          className: 'g-pill',
        });
      }
      if (inert) continue;
      const shown = view.line(span.row).slice(span.column - 1, span.column - 1 + span.length);
      targets.push({
        id,
        span,
        action: { type: 'key', key: item.key, guard: { span, text: shown } },
        label: item.label === '' ? keyName(item.key) : `${item.label} (${keyName(item.key)})`,
        box: id,
      });
    }
  }
  return { boxes, text, targets };
}

/** A key as the keyboard calls it: Roll Up is Page Down. */
export function keyName(key: AidKeyName): string {
  if (key === 'RollUp') return 'Page Down';
  if (key === 'RollDown') return 'Page Up';
  return key;
}
