// Input fields as boxes: one per row a field covers, with the field's rules from its
// field format word. Bypass fields stay plain text. Fields behind an active window are
// drawn inert.

import { isNondisplay, type FieldFormat, type SnapshotField } from '../protocol/index.ts';
import type { Box, Span, TextStyle } from '../decorations.ts';
import type { Recognized } from '../recognize/index.ts';
import { contains, isRed, isReverse, type ScreenView } from '../recognize/view.ts';

export interface FieldDecorations {
  readonly text: TextStyle[];
  readonly masks: Span[];
  readonly boxes: Box[];
  readonly info: string | undefined;
}

export function decorateFields(view: ScreenView, recognized: Recognized): FieldDecorations {
  const { snapshot, columns } = view;
  const cursor = (snapshot.cursor.row - 1) * columns + snapshot.cursor.column - 1;
  const errorShown = recognized.message?.kind === 'error';
  const { window } = recognized;
  const text: TextStyle[] = [];
  const masks: Span[] = [];
  const boxes: Box[] = [];
  let info: string | undefined;
  // The filter row of a table: its fields look like search boxes.
  const filters = new Set(
    recognized.scopes.flatMap((scope) =>
      scope.subfiles.flatMap((subfile) =>
        (subfile.filter?.fields ?? []).map((field) => `${field.row}:${field.column}`),
      ),
    ),
  );
  for (const field of view.inputs) {
    // The attribute in effect, not the field's own: the host rewrites only the attribute
    // byte when it reverses a field in error.
    const attribute = view.attribute(field.row, field.column);
    const start = (field.row - 1) * columns + field.column - 1;
    const focus = cursor >= start && cursor < start + field.length;
    const secret = isNondisplay(attribute);
    const reverse = isReverse(attribute);
    const { format } = field;
    const modifiers: string[] = [];
    if (focus) modifiers.push('focus');
    if (errorShown && (focus || reverse)) modifiers.push('error');
    if (format.mandatoryEnter || format.adjust === 'mandatory-fill') modifiers.push('required');
    if (NUMERIC.has(format.shift)) modifiers.push('numeric');
    if (secret) modifiers.push('secret');
    if (reverse) modifiers.push('reverse');
    if (isRed(attribute)) modifiers.push('red');
    if (filters.has(`${field.row}:${field.column}`)) modifiers.push('search');
    if (window && !contains(window.content, field.row, field.column)) modifiers.push('inert');
    for (const span of fieldSpans(field, columns)) {
      boxes.push({
        id: `field:${span.row}:${span.column}`,
        kind: 'field',
        rect: { row: span.row, column: span.column, rows: 1, columns: span.length },
        modifiers,
      });
      text.push({ span, className: 'g-input' });
      if (secret) masks.push(span);
    }
    if (focus) info = fieldRules(format, field.length, secret);
  }
  return { text, masks, boxes, info };
}

const NUMERIC = new Set(['numeric-only', 'digits-only', 'signed-numeric']);

/** The cells of a field, one span per row it covers. */
export function fieldSpans(field: SnapshotField, columns: number): Span[] {
  const spans: Span[] = [];
  let index = (field.row - 1) * columns + field.column - 1;
  let left = field.length;
  while (left > 0) {
    const column = (index % columns) + 1;
    const length = Math.min(left, columns - column + 1);
    spans.push({ row: Math.floor(index / columns) + 1, column, length });
    index += length;
    left -= length;
  }
  return spans;
}

const SHIFTS: Record<FieldFormat['shift'], string | undefined> = {
  'alpha-shift': undefined,
  'alpha-only': 'Letters only',
  'numeric-shift': 'Numeric shift',
  'numeric-only': 'Numbers only',
  'katakana-shift': 'Katakana',
  'digits-only': 'Digits only',
  io: 'No typing (I/O field)',
  'signed-numeric': 'Signed number',
};

/** A field's rules in words, for example "Numbers only · 10 positions · Required". */
export function fieldRules(format: FieldFormat, length: number, secret: boolean): string {
  const parts: string[] = [];
  const shift = SHIFTS[format.shift];
  if (shift) parts.push(shift);
  if (format.monocase && ['alpha-shift', 'alpha-only', 'numeric-shift'].includes(format.shift)) {
    parts.push('Uppercase');
  }
  if (format.shift === 'signed-numeric') parts.push(`${length - 1} digits`);
  else parts.push(length === 1 ? '1 position' : `${length} positions`);
  if (format.mandatoryEnter) parts.push('Required');
  if (format.adjust === 'mandatory-fill') parts.push('Fill every position');
  if (format.adjust === 'right-zero') parts.push('Right-adjusts with zeros');
  if (format.adjust === 'right-blank') parts.push('Right-adjusts');
  if (format.fieldExitRequired) parts.push('Needs Field Exit');
  if (format.autoEnter) parts.push('Sends when full');
  if (secret) parts.push('Hidden');
  return parts.join(' · ');
}
