// Subfile tables: zebra stripes under the records, a band behind the headings (and the
// filter row, whose fields look like search boxes), a band that lights the hovered record,
// the More... / Bottom marker as a pager button, and amounts with credits marked. Every
// character stays in its cell. A table behind an active window is drawn inert, with no
// button.

import type { Box, Rect, Table, Target, TextStyle } from '../decorations.ts';
import type { Scope } from '../recognize/index.ts';
import { findAmounts } from '../recognize/numbers.ts';
import type { Subfile } from '../recognize/subfile.ts';
import type { ScreenView } from '../recognize/view.ts';
import { keyName } from './keys.ts';

export interface SubfileDecorations {
  readonly boxes: Box[];
  readonly text: TextStyle[];
  readonly targets: Target[];
  readonly tables: Table[];
}

export function decorateSubfiles(scope: Scope): SubfileDecorations {
  const result: SubfileDecorations = { boxes: [], text: [], targets: [], tables: [] };
  for (const subfile of scope.subfiles) decorateSubfile(scope, subfile, result);
  return result;
}

function decorateSubfile(scope: Scope, subfile: Subfile, out: SubfileDecorations): void {
  const { view, inert } = scope;
  const modifiers = inert ? ['inert'] : [];
  const { left, right, records, step, lastRow, headings, filter, marker } = subfile;
  const columns = right - left + 1;
  const firstRow = records[0];
  const id = `table:${firstRow}`;

  // Records: stripes on every second record, and one band per record for the hover.
  out.boxes.push({
    id,
    kind: 'table',
    rect: { row: firstRow, column: left, rows: lastRow - firstRow + 1, columns },
    modifiers: step > 1 ? [...modifiers, `step${step}`] : modifiers,
  });
  const recordRects: Rect[] = records.map((row) => ({
    row,
    column: left,
    rows: Math.min(step, lastRow - row + 1),
    columns,
  }));
  if (!inert) {
    for (const rect of recordRects) {
      out.boxes.push({ id: `band:${rect.row}`, kind: 'band', rect, modifiers: [] });
    }
  }

  // Headings, and the filter row below them.
  const bandTop = headings[0] ?? filter?.row;
  const bandBottom = filter?.row ?? headings.at(-1);
  if (bandTop !== undefined && bandBottom !== undefined) {
    out.boxes.push({
      id: `heading:${bandTop}`,
      kind: 'heading',
      rect: { row: bandTop, column: left, rows: bandBottom - bandTop + 1, columns },
      modifiers: filter ? [...modifiers, 'filtered'] : modifiers,
    });
  }
  for (const row of headings) {
    out.text.push({ span: { row, column: left, length: columns }, className: 'g-heading' });
  }
  if (filter && subfile.option) {
    const { column, length } = subfile.option;
    const free =
      view
        .line(filter.row)
        .slice(column - 1, column - 1 + length)
        .trim() === '';
    if (free && filter.fields.every((field) => field.column > column + length)) {
      out.boxes.push({
        id: `search:${filter.row}`,
        kind: 'search',
        rect: { row: filter.row, column, rows: 1, columns: length },
        modifiers,
      });
    }
  }

  // Amounts: credits in red. Text inside an input field is the operator's, not data.
  for (let row = firstRow; row <= lastRow; row++) {
    for (const amount of findAmounts(view.line(row))) {
      if (inField(view, row, amount.column, amount.length)) continue;
      const span = { row, column: amount.column, length: amount.length };
      out.text.push({ span, className: amount.credit ? 'g-num g-credit' : 'g-num' });
    }
  }

  // The marker as a button: More... pages forward, Bottom back.
  if (marker) {
    const pagerId = `pager:${marker.row}`;
    const { span } = marker;
    out.boxes.push({
      id: pagerId,
      kind: 'pager',
      rect: { row: span.row, column: span.column, rows: 1, columns: span.length },
      modifiers: [...modifiers, marker.more ? 'more' : 'bottom'],
    });
    out.text.push({ span, className: 'g-pager' });
    if (!inert) {
      const key = marker.more ? 'RollUp' : 'RollDown';
      out.targets.push({
        id: pagerId,
        span,
        action: { type: 'key', key, guard: { span, text: marker.text } },
        label: marker.more ? `Show more (${keyName(key)})` : `Back one page (${keyName(key)})`,
        box: pagerId,
      });
    }
  }

  out.tables.push({ id, inert, records: recordRects });
}

function inField(view: ScreenView, row: number, column: number, length: number): boolean {
  return view.inputs.some(
    (field) =>
      field.row === row && column < field.column + field.length && column + length > field.column,
  );
}
