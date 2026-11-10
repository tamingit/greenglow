// Row 1 as a header: a band, the program or format ID (for example "PGM100/CTL1" at
// column 2) as a chip, and the title.

import type { Span } from '../decorations.ts';
import { isHighIntensity, type ScreenView } from './view.ts';

export interface Header {
  readonly id: { readonly span: Span; readonly text: string } | undefined;
  readonly title: Span | undefined;
}

export interface Segment {
  readonly column: number;
  readonly text: string;
}

/** An IBM i name (up to 10 characters), with an optional "/FORMAT". */
const ID = /^[A-Za-z@#$][\w@#$]{0,9}(?:\/[A-Za-z@#$][\w@#$]{0,9})?$/;
const DATE_OR_TIME = /\d{1,2}[:/]\d{2}/;

export function recognizeHeader(view: ScreenView, claimed: Set<number>): Header | undefined {
  if (claimed.has(1) || view.line(1).trim() === '') return undefined;
  if (view.inputs.some((field) => field.row === 1)) return undefined;
  const parts = segments(view, 1);
  const [first] = parts;
  let id: Header['id'];
  if (first && first.column <= 4 && ID.test(first.text) && idShape(first.text, parts.length > 1)) {
    id = { span: { row: 1, column: first.column, length: first.text.length }, text: first.text };
  }
  const rest = id ? parts.slice(1) : parts;
  const title = pickTitle(view, rest);
  return {
    id,
    title: title && { row: 1, column: title.column, length: title.text.length },
  };
}

/**
 * Words that look like an ID rather than a title: a "/", a digit, a camel hump
 * ("OrdentryH"), or all capitals beside other text ("MAIN" next to its title). That
 * keeps out words such as "Select" and environment names longer than 10 characters.
 */
function idShape(text: string, hasOtherText: boolean): boolean {
  if (text.includes('/') || /\d/.test(text) || /[a-z][A-Z]/.test(text)) return true;
  return hasOtherText && /^[A-Z@#$][A-Z0-9@#$_]{2,}$/.test(text);
}

/** The title: in high intensity, else centred, else the only wordy segment. */
function pickTitle(view: ScreenView, parts: readonly Segment[]): Segment | undefined {
  const bright = parts.filter((part) => allHighIntensity(view, part));
  if (bright.length > 0) return bright.reduce((a, b) => (b.text.length > a.text.length ? b : a));
  const lettered = parts.filter((part) => /[A-Za-z]/.test(part.text));
  const centre = (view.columns + 1) / 2;
  const centred = lettered.filter(
    (part) => Math.abs(part.column + (part.text.length - 1) / 2 - centre) <= view.columns * 0.15,
  );
  if (centred.length === 1) return centred[0];
  const wordy = lettered.filter(
    (part) => (part.text.match(/[A-Za-z]{2,}/g) ?? []).length >= 2 && !DATE_OR_TIME.test(part.text),
  );
  return wordy.length === 1 ? wordy[0] : undefined;
}

function allHighIntensity(view: ScreenView, part: Segment): boolean {
  for (let i = 0; i < part.text.length; i++) {
    if (part.text[i] !== ' ' && !isHighIntensity(view.attribute(1, part.column + i))) return false;
  }
  return true;
}

/**
 * A row split into segments at two or more spaces, and at an attribute cell that changes
 * the look between two words.
 */
export function segments(view: ScreenView, row: number): Segment[] {
  const line = view.line(row);
  const parts: Segment[] = [];
  let start = -1;
  for (let i = 0; i <= line.length; i++) {
    if (i < line.length && line[i] !== ' ') {
      if (start < 0) start = i;
      continue;
    }
    if (start < 0) continue;
    const single = i + 1 < line.length && line[i + 1] !== ' ';
    // Column i + 1 is this blank; the words around it are at columns i and i + 2.
    const lookChanges =
      view.isAttributeCell(row, i + 1) && view.attribute(row, i) !== view.attribute(row, i + 2);
    if (single && !lookChanges) continue;
    parts.push({ column: start + 1, text: line.slice(start, i) });
    start = -1;
  }
  return parts;
}
