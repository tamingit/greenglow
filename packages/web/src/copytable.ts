// Copying a table (a subfile the recognizers found) as tab-separated text, which a
// spreadsheet pastes as columns: its heading rows, then a line per record. Columns are the
// runs of characters with two or more blank columns between them in every row of the table,
// so a heading and the data under it share a column. The option field (what the user types) is left out,
// and nondisplay text is already blank in the view.
//
// Copy entire table pages through the list with Page Down, which the user asked for, and
// collects every page until Bottom, or with no marker until the IBM i refuses Page Down or
// shows the same page again (see main.ts). This module only reads one screen.

import type { ScreenSnapshot } from './protocol/index.ts';
import { recognize } from './recognize/index.ts';
import type { Subfile } from './recognize/subfile.ts';
import { screenView, type ScreenView } from './recognize/view.ts';

export interface TablePage {
  /** The heading rows, each split into columns. */
  readonly headings: readonly string[][];
  /** The records, each split into columns (a record of several rows is one line). */
  readonly records: readonly string[][];
  /**
   * There may be more to roll to: the marker says More... or +, or there is no marker (some
   * screens don't show one). Only Bottom says there isn't.
   */
  readonly more: boolean;
  /** The screen shows a marker (More..., + or Bottom). */
  readonly marked: boolean;
  /** The records as text, to tell one page from the next. */
  readonly key: string;
}

/** The live table on the screen (not one behind a window), if the recognizers found one. */
export function liveTable(
  snapshot: ScreenSnapshot,
): { view: ScreenView; subfile: Subfile } | undefined {
  const { scopes } = recognize(screenView(snapshot));
  for (const scope of scopes) {
    if (scope.inert) continue;
    const subfile = scope.subfiles[0];
    if (subfile) return { view: scope.view, subfile };
  }
  return undefined;
}

/** Reads the table on the screen, or undefined when there is none. */
export function readTable(snapshot: ScreenSnapshot): TablePage | undefined {
  const found = liveTable(snapshot);
  if (!found) return undefined;
  const { view, subfile } = found;
  const blankOption = (row: number, text: string): string => {
    const option = subfile.option;
    if (!option || !subfile.records.some((r) => row >= r && row < r + subfile.step)) return text;
    const at = option.column - 1;
    return text.slice(0, at) + ' '.repeat(option.length) + text.slice(at + option.length);
  };
  const line = (row: number) => blankOption(row, view.line(row));
  // One set of columns per row of a record (most records are one row); the headings share
  // the first.
  const records: string[][] = subfile.records.map(() => []);
  let headings: string[][] = [];
  for (let k = 0; k < subfile.step; k++) {
    const rows = subfile.records.map((r) => line(r + k));
    const heads = k === 0 ? subfile.headings.map((r) => line(r)) : [];
    // A column with nothing in any record (the heading over the option field) is left out.
    const spans = columns([...heads, ...rows]).filter(([from, to]) =>
      rows.some((text) => text.slice(from, to).trim() !== ''),
    );
    if (k === 0) headings = heads.map((text) => cells(text, spans));
    rows.forEach((text, i) => records[i].push(...cells(text, spans)));
  }
  return {
    headings: headings.filter((h) => h.some((c) => c !== '')),
    records,
    more: subfile.marker?.more ?? true,
    marked: subfile.marker !== undefined,
    key: records.map((r) => r.join('\t')).join('\n'),
  };
}

/**
 * The runs of columns with text in some line, split where two or more columns are blank in
 * every line (a single blank is a space inside a column, as in a description).
 */
function columns(lines: readonly string[]): [number, number][] {
  const width = Math.max(0, ...lines.map((l) => l.length));
  const used = (i: number) => lines.some((l) => (l[i] ?? ' ') !== ' ');
  const spans: [number, number][] = [];
  let start = -1;
  for (let i = 0; i <= width; i++) {
    if (i < width && used(i)) {
      if (start < 0) start = i;
      continue;
    }
    if (start < 0 || (i + 1 < width && used(i + 1))) continue;
    spans.push([start, i]);
    start = -1;
  }
  return spans;
}

function cells(text: string, spans: readonly [number, number][]): string[] {
  return spans.map(([from, to]) => text.slice(from, to).trim());
}

/**
 * Tab-separated text for a spreadsheet: headings, then records. A cell a spreadsheet would
 * run as a formula is defused with a leading apostrophe, as the admin page's CSV does.
 */
export function toTsv(headings: readonly string[][], records: readonly string[][]): string {
  const safe = (cell: string) =>
    /^[=+\-@]/.test(cell) && !/^-?[\d.,]+-?$/.test(cell) ? `'${cell}` : cell;
  return [...headings, ...records].map((row) => row.map(safe).join('\t')).join('\r\n') + '\r\n';
}
