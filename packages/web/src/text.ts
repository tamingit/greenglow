// The text layer: a row of the regeneration buffer as runs of cells that share a look,
// classed by field attribute as a colour 5250 shows it (SC30-3533-04 table 15-10).

import {
  displayCharacter,
  isAttribute,
  isNondisplay,
  type ScreenSnapshot,
} from './protocol/index.ts';
import type { Span, TextStyle } from './decorations.ts';

/**
 * Classes for attributes 0x20–0x3F (table 15-10): colour, then reverse (rev), underline
 * (ul), blink and column separators (cs). Nondisplay (nd) hides the text.
 */
export const ATTRIBUTE_CLASSES = [
  'c-green',
  'c-green rev',
  'c-white',
  'c-white rev',
  'c-green ul',
  'c-green ul rev',
  'c-white ul',
  'nd',
  'c-red',
  'c-red rev',
  'c-red blink',
  'c-red rev blink',
  'c-red ul',
  'c-red ul rev',
  'c-red ul blink',
  'nd',
  'c-turquoise cs',
  'c-turquoise cs rev',
  'c-yellow cs',
  'c-yellow cs rev',
  'c-turquoise ul',
  'c-turquoise ul rev',
  'c-yellow ul',
  'nd',
  'c-pink',
  'c-pink rev',
  'c-blue',
  'c-blue rev',
  'c-pink ul',
  'c-pink ul rev',
  'c-blue ul',
  'nd',
];

export interface Run {
  className: string;
  text: string;
}

/** A look's additions to one row, by column (0-based). */
export interface RowStyle {
  /** Extra class names, added after the attribute's classes. */
  readonly classes: (string | undefined)[];
  /** Cells of nondisplay input: a typed character shows as a bullet. */
  readonly masked: boolean[];
}

/** A row of the screen: its bytes and the attribute in effect at each one. */
export interface Line {
  bytes: Uint8Array;
  attributes: Uint8Array;
}

/** The screen's rows, then the separate message line if there is one. */
export function screenLines(snapshot: ScreenSnapshot): Line[] {
  const { rows, columns, buffer, attributes } = snapshot;
  const lines: Line[] = [];
  for (let r = 0; r < rows; r++) {
    const start = r * columns;
    lines.push({
      bytes: buffer.subarray(start, start + columns),
      attributes: attributes.subarray(start, start + columns),
    });
  }
  if (snapshot.messageLine) {
    const line = snapshot.messageLine;
    const effective = new Uint8Array(columns);
    let current = 0x20;
    line.forEach((b, i) => (effective[i] = current = isAttribute(b) ? b : current));
    lines.push({ bytes: line, attributes: effective });
  }
  return lines;
}

/** A row as runs of cells that share a look. Attribute cells show as plain blanks. */
export function rowRuns(bytes: Uint8Array, attributes: Uint8Array, style?: RowStyle): Run[] {
  const runs: Run[] = [];
  for (let i = 0; i < bytes.length; i++) {
    const byte = bytes[i];
    const attribute = attributes[i];
    let className: string;
    let char: string;
    if (isAttribute(byte)) {
      className = 'attribute';
      char = ' ';
    } else {
      className = ATTRIBUTE_CLASSES[attribute - 0x20] ?? 'c-green';
      if (style?.masked[i]) {
        // Only whether a position holds something; the character is never decoded.
        char = byte === 0x00 || byte === 0x40 ? ' ' : '•';
      } else {
        char = isNondisplay(attribute) ? ' ' : displayCharacter(byte);
      }
    }
    const extra = style?.classes[i];
    if (extra) className += ` ${extra}`;
    const last = runs.at(-1);
    if (last?.className === className) last.text += char;
    else runs.push({ className, text: char });
  }
  return runs;
}

/** Row styles from a look's text classes and masks, indexed by 0-based line. */
export function rowStyles(
  text: readonly TextStyle[],
  masks: readonly Span[],
  lines: number,
  columns: number,
): (RowStyle | undefined)[] {
  const styles: (RowStyle | undefined)[] = new Array<RowStyle | undefined>(lines);
  const styleOf = (row: number) =>
    (styles[row - 1] ??= {
      classes: new Array<string | undefined>(columns),
      masked: new Array<boolean>(columns).fill(false),
    });
  for (const { span, className } of text) {
    if (span.row < 1 || span.row > lines) continue;
    const { classes } = styleOf(span.row);
    for (let c = span.column - 1; c < span.column - 1 + span.length && c < columns; c++) {
      classes[c] = classes[c] ? `${classes[c]} ${className}` : className;
    }
  }
  for (const span of masks) {
    if (span.row < 1 || span.row > lines) continue;
    const { masked } = styleOf(span.row);
    for (let c = span.column - 1; c < span.column - 1 + span.length && c < columns; c++) {
      masked[c] = true;
    }
  }
  return styles;
}
