// The screen as recognizers read it: text by row and the attribute in effect at each
// position. Nondisplay contents are never decoded, so no recognizer can see them. A scoped
// view shows only part of the screen (a window's inside, or the screen around it), so the
// same recognizers can run once per scope.

import {
  displayCharacter,
  isAttribute,
  isNondisplay,
  type ScreenSnapshot,
  type SnapshotField,
} from '../protocol/index.ts';
import type { Rect } from '../decorations.ts';

export interface ScreenView {
  readonly snapshot: ScreenSnapshot;
  readonly rows: number;
  readonly columns: number;
  /**
   * The display text of a row, 1-based; row `rows + 1` is the separate message line when
   * there is one. Attribute cells and nondisplay text are spaces.
   */
  line(row: number): string;
  /** The attribute in effect at a position (0x20–0x3F). */
  attribute(row: number, column: number): number;
  isAttributeCell(row: number, column: number): boolean;
  /** Fields the operator can key into (not bypass), in screen order. */
  readonly inputs: readonly SnapshotField[];
  /**
   * The part of the screen this view shows: the whole screen (with the message line), or
   * a window's inside. Text outside it, and in any hole, reads as blanks.
   */
  readonly bounds: Rect;
  readonly scope: 'screen' | 'window';
}

interface Line {
  bytes: Uint8Array;
  attributes: Uint8Array;
  text: string | undefined;
}

export function screenView(snapshot: ScreenSnapshot): ScreenView {
  const { rows, columns } = snapshot;
  const lines: (Line | undefined)[] = [];

  function lineAt(row: number): Line | undefined {
    if (row < 1) return undefined;
    const cached = lines[row];
    if (cached) return cached;
    let line: Line;
    if (row <= rows) {
      const start = (row - 1) * columns;
      line = {
        bytes: snapshot.buffer.subarray(start, start + columns),
        attributes: snapshot.attributes.subarray(start, start + columns),
        text: undefined,
      };
    } else if (row === rows + 1 && snapshot.messageLine) {
      const bytes = snapshot.messageLine;
      const attributes = new Uint8Array(bytes.length);
      let current = 0x20;
      bytes.forEach((b, i) => (attributes[i] = current = isAttribute(b) ? b : current));
      line = { bytes, attributes, text: undefined };
    } else {
      return undefined;
    }
    lines[row] = line;
    return line;
  }

  return {
    snapshot,
    rows,
    columns,
    line(row) {
      const line = lineAt(row);
      if (!line) return '';
      line.text ??= Array.from(line.bytes, (byte, i) =>
        isAttribute(byte) || isNondisplay(line.attributes[i]) ? ' ' : displayCharacter(byte),
      ).join('');
      return line.text;
    },
    attribute(row, column) {
      return lineAt(row)?.attributes[column - 1] ?? 0x20;
    },
    isAttributeCell(row, column) {
      const byte = lineAt(row)?.bytes[column - 1];
      return byte !== undefined && isAttribute(byte);
    },
    inputs: snapshot.fields.filter((field) => !field.format.bypass),
    bounds: { row: 1, column: 1, rows: rows + 1, columns },
    scope: 'screen',
  };
}

/**
 * The part of a view inside `bounds` and outside every hole: for a window's inside, or
 * for the screen around a window. Inputs are the ones that start there.
 */
export function scopedView(
  view: ScreenView,
  scope: ScreenView['scope'],
  bounds: Rect,
  holes: readonly Rect[] = [],
): ScreenView {
  const shows = (row: number, column: number) =>
    contains(bounds, row, column) && !holes.some((hole) => contains(hole, row, column));
  const lines: string[] = [];
  return {
    snapshot: view.snapshot,
    rows: view.rows,
    columns: view.columns,
    line(row) {
      lines[row] ??= Array.from(view.line(row), (char, i) => (shows(row, i + 1) ? char : ' ')).join(
        '',
      );
      return lines[row];
    },
    attribute: (row, column) => view.attribute(row, column),
    isAttributeCell: (row, column) => shows(row, column) && view.isAttributeCell(row, column),
    inputs: view.inputs.filter((field) => shows(field.row, field.column)),
    bounds,
    scope,
  };
}

export function contains(rect: Rect, row: number, column: number): boolean {
  return (
    row >= rect.row &&
    row < rect.row + rect.rows &&
    column >= rect.column &&
    column < rect.column + rect.columns
  );
}

/** Buffer positions inside any field, bypass fields included. */
export function fieldCells(view: ScreenView): Set<number> {
  const cells = new Set<number>();
  for (const field of view.snapshot.fields) {
    const start = (field.row - 1) * view.columns + field.column - 1;
    for (let i = start; i < start + field.length; i++) cells.add(i);
  }
  return cells;
}

/** High intensity: white, in table 15-10's colour mapping. */
export function isHighIntensity(attribute: number): boolean {
  return attribute === 0x22 || attribute === 0x23 || attribute === 0x26;
}

export function isRed(attribute: number): boolean {
  return attribute >= 0x28 && attribute <= 0x2e;
}

/** Reverse image (the low bit), except the nondisplay attributes, which share it. */
export function isReverse(attribute: number): boolean {
  return (attribute & 0x01) === 1 && !isNondisplay(attribute);
}
