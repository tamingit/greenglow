// Subfile tables: a list of records on the screen. Two shapes are recognized from the
// stream alone.
//
// An option list has a column of short input fields, one per record (the option field),
// with nothing to their left and the record's data to their right. Above it may sit a
// filter row (input fields), up to two heading rows and the legend of option codes
// ("2=Update  4=Delete"); below it a marker ("More...", "Bottom") says whether there is
// more to roll to.
//
// A display-only list has no option field: a marker with at least three rows of columns
// of text above it and no input fields among them. Display Messages, help prose and
// order-entry grids (whose rows hold input fields) are not lists.
//
// Both are conservative: when a rule is unsure, nothing is recognized and the screen
// shows as it is.

import type { SnapshotField } from '../protocol/index.ts';
import type { Span } from '../decorations.ts';
import { tokenize, type CodeItem } from './codes.ts';
import { segments } from './header.ts';
import { fieldCells, isHighIntensity, isReverse, type ScreenView } from './view.ts';

export interface OptionLegend {
  readonly row: number;
  readonly items: readonly CodeItem[];
}

export interface Marker {
  readonly row: number;
  readonly span: Span;
  /** "More...", "Bottom" or "+". */
  readonly text: string;
  readonly more: boolean;
}

export interface Subfile {
  readonly kind: 'options' | 'list';
  /** The option column: where the option field of each record starts and its length. */
  readonly option: { readonly column: number; readonly length: number } | undefined;
  /** The first row of each record, and how many rows a record takes. */
  readonly records: readonly number[];
  readonly step: number;
  /** The last row of the last record. */
  readonly lastRow: number;
  readonly headings: readonly number[];
  readonly filter: { readonly row: number; readonly fields: readonly SnapshotField[] } | undefined;
  readonly legend: readonly OptionLegend[];
  readonly marker: Marker | undefined;
  /** The columns the table covers, from the option field's attribute cell to its data. */
  readonly left: number;
  readonly right: number;
}

/** Option codes are 1–3 letters or digits. */
const OPTION_CODES = '[A-Za-z0-9]{1,3}';
const MARKER = /^(More\.\.\.|Bottom|\+)$/;
/** How far above a table its option legend may sit, and how many legend rows it has. */
const LEGEND_REACH = 8;
const LEGEND_ROWS = 6;

export function recognizeSubfiles(view: ScreenView, claimed: Set<number>): Subfile[] {
  const context = new Context(view, claimed);
  const found: Subfile[] = [];
  for (const run of optionRuns(context)) {
    const first = run.records[0];
    const last = run.records.at(-1) ?? first;
    // One table per stretch of rows.
    if (found.some((s) => first <= s.lastRow && last + run.step - 1 >= s.records[0])) continue;
    const subfile = optionList(context, run);
    found.push(subfile);
    claim(claimed, subfile);
  }
  if (found.length === 0) {
    const list = displayList(context);
    if (list) {
      found.push(list);
      claim(claimed, list);
    }
  }
  return found;
}

function claim(claimed: Set<number>, subfile: Subfile): void {
  const rows = [
    ...subfile.headings,
    ...subfile.legend.map((l) => l.row),
    ...(subfile.filter ? [subfile.filter.row] : []),
    ...(subfile.marker ? [subfile.marker.row] : []),
  ];
  for (const row of rows) claimed.add(row);
  for (const start of subfile.records) {
    for (let row = start; row < start + subfile.step; row++) claimed.add(row);
  }
}

/** What the rules share: the view, the claimed rows and each row's input fields. */
class Context {
  readonly view: ScreenView;
  readonly claimed: Set<number>;
  readonly first: number;
  readonly last: number;
  readonly #inputs = new Map<number, SnapshotField[]>();
  #cells: Set<number> | undefined;

  constructor(view: ScreenView, claimed: Set<number>) {
    this.view = view;
    this.claimed = claimed;
    // Row 1 belongs to the header.
    this.first = Math.max(2, view.bounds.row);
    this.last = Math.min(view.rows, view.bounds.row + view.bounds.rows - 1);
    for (const field of view.inputs) {
      const row = this.#inputs.get(field.row);
      if (row) row.push(field);
      else this.#inputs.set(field.row, [field]);
    }
  }

  inputsOn(row: number): readonly SnapshotField[] {
    return this.#inputs.get(row) ?? [];
  }

  /** No text and no input field. */
  blank(row: number): boolean {
    return this.view.line(row).trim() === '' && this.inputsOn(row).length === 0;
  }

  available(row: number): boolean {
    return row >= this.first && row <= this.last && !this.claimed.has(row);
  }

  get cells(): Set<number> {
    return (this.#cells ??= fieldCells(this.view));
  }
}

interface Run {
  readonly column: number;
  readonly length: number;
  readonly records: readonly number[];
  readonly step: number;
}

/**
 * Columns of short input fields, one per record at a constant step of 1 to 3 rows. (When
 * a window opens over a list, the host's new format table drops the list's fields, so a
 * list behind a window is no longer found.)
 */
function optionRuns(context: Context): Run[] {
  const groups = new Map<string, SnapshotField[]>();
  for (const field of context.view.inputs) {
    if (field.length > 3 || !context.available(field.row)) continue;
    const key = `${field.column}:${field.length}`;
    const group = groups.get(key);
    if (group) group.push(field);
    else groups.set(key, [field]);
  }
  const runs: Run[] = [];
  const ordered = [...groups.values()].sort((a, b) => a[0].column - b[0].column);
  for (const group of ordered) {
    const { column, length } = group[0];
    const rows = [...new Set(group.map((field) => field.row))].sort((a, b) => a - b);
    let current: number[] = [];
    const finish = () => {
      const step = current.length > 1 ? current[1] - current[0] : 0;
      if (step >= 1 && step <= 3 && current.length >= (step === 1 ? 2 : 3)) {
        const run = { column, length, records: current, step };
        if (leftIsBlank(context, run) && hasData(context, run)) runs.push(run);
      }
      current = [];
    };
    for (const row of rows) {
      const n = current.length;
      if (n > 1 && row - current[n - 1] !== current[1] - current[0]) finish();
      current.push(row);
    }
    finish();
  }
  return runs;
}

/** Nothing to the left of the option field on its row: no text and no other field. */
function leftIsBlank(context: Context, run: Run): boolean {
  const { view } = context;
  const from = view.bounds.column - 1;
  return run.records.every((row) => {
    if (
      view
        .line(row)
        .slice(from, run.column - 1)
        .trim() !== ''
    )
      return false;
    for (let c = from; c < run.column - 1; c++) {
      if (context.cells.has((row - 1) * view.columns + c)) return false;
    }
    return true;
  });
}

/** At least half the records have something to the right of the option field. */
function hasData(context: Context, run: Run): boolean {
  const rows = run.records;
  const withData = rows.filter(
    (row) =>
      context.view
        .line(row)
        .slice(run.column - 1 + run.length)
        .trim() !== '',
  );
  return rows.length > 0 && withData.length * 2 >= rows.length;
}

function optionList(context: Context, run: Run): Subfile {
  const first = run.records[0];
  const lastRow = Math.min(context.last, run.records[run.records.length - 1] + run.step - 1);
  // Walk up from the first record: a blank row, the filter row, then the headings.
  let row = first - 1;
  let skipped = false;
  if (context.available(row) && context.blank(row)) {
    skipped = true;
    row--;
  }
  let filter: Subfile['filter'];
  const fields = context.inputsOn(row);
  if (context.available(row) && fields.length >= 2) {
    filter = { row, fields };
    row--;
  }
  const headings: number[] = [];
  if (!skipped || filter) {
    while (headings.length < 2 && isHeading(context, row, run.length)) headings.unshift(row--);
  }
  const top = headings[0] ?? filter?.row ?? first;
  const legend = optionLegend(context, top - 1, run.length);
  const marker = markerBelow(context, lastRow + 1);
  const body: number[] = [];
  for (let r = first; r <= lastRow; r++) body.push(r);
  const rows = [...headings, ...(filter ? [filter.row] : []), ...body];
  return {
    kind: 'options',
    option: { column: run.column, length: run.length },
    records: run.records,
    step: run.step,
    lastRow,
    headings,
    filter,
    legend,
    marker,
    ...extent(context, rows, run.column - 1),
  };
}

/** A heading row: text in at least two segments, no input fields, not a legend. */
function isHeading(context: Context, row: number, optionLength: number): boolean {
  if (!context.available(row) || context.blank(row) || context.inputsOn(row).length > 0) {
    return false;
  }
  if (legendOn(context, row, optionLength)) return false;
  return segments(context.view, row).length >= 2;
}

/** The option legends within reach above `from`: rows of "code=label" items. */
function optionLegend(context: Context, from: number, length: number): OptionLegend[] {
  const legends: OptionLegend[] = [];
  const stop = Math.max(context.first, from - LEGEND_REACH + 1);
  for (let row = from; row >= stop && legends.length < LEGEND_ROWS; row--) {
    if (!context.available(row)) continue;
    const legend = legendOn(context, row, length);
    if (legend) legends.unshift(legend);
  }
  return legends;
}

/**
 * A row of option codes and labels. One leading word is allowed ("Options:" or "Option"),
 * and every code must fit the option field.
 */
function legendOn(context: Context, row: number, length: number): OptionLegend | undefined {
  const { view } = context;
  if (context.inputsOn(row).length > 0) return undefined;
  const { items, rest } = tokenize(view.line(row), OPTION_CODES);
  if (items.length === 0 || !/^\s*(?:[A-Za-z]+:?)?\s*$/.test(rest)) return undefined;
  for (const item of items) {
    if (item.code.length > length || item.label === '') return undefined;
    for (let c = item.column; c < item.end; c++) {
      if (context.cells.has((row - 1) * view.columns + c - 1)) return undefined;
    }
  }
  return { row, items };
}

/** The first non-blank row at or below `from`, if it holds only a marker. */
function markerBelow(context: Context, from: number): Marker | undefined {
  for (let row = from; row <= context.last; row++) {
    if (!context.available(row)) return undefined;
    const line = context.view.line(row);
    if (line.trim() === '' && context.inputsOn(row).length === 0) continue;
    return markerOn(context, row);
  }
  return undefined;
}

function markerOn(context: Context, row: number): Marker | undefined {
  const line = context.view.line(row);
  const text = line.trim();
  if (!MARKER.test(text) || context.inputsOn(row).length > 0) return undefined;
  const column = line.indexOf(text) + 1;
  return { row, span: { row, column, length: text.length }, text, more: text !== 'Bottom' };
}

/**
 * The columns a set of rows covers: from `left` (or the first text, when there is none)
 * to just past the last text or field.
 */
function extent(
  context: Context,
  rows: readonly number[],
  left: number | undefined,
): { left: number; right: number } {
  const { view } = context;
  const { bounds } = view;
  let start = Infinity;
  let end = 0;
  for (const row of rows) {
    const line = view.line(row);
    const text = line.trim();
    if (text !== '') {
      start = Math.min(start, line.indexOf(text));
      end = Math.max(end, line.trimEnd().length + 1);
    }
    for (const field of context.inputsOn(row)) end = Math.max(end, field.column + field.length);
  }
  const first = bounds.column;
  const last = bounds.column + bounds.columns - 1;
  const from = Math.max(first, left ?? (Number.isFinite(start) ? start : first));
  return { left: from, right: Math.min(last, Math.max(end, from)) };
}

/**
 * A marker with rows of text above it, none of which hold input fields. The walk up
 * stops at a blank row, at an instruction row (all blue) and at a row taken by another
 * recognizer; the leading heading rows are white or reversed.
 */
function displayList(context: Context): Subfile | undefined {
  const { view } = context;
  let marker: Marker | undefined;
  for (let row = context.last; row >= context.first && !marker; row--) {
    if (context.available(row)) marker = markerOn(context, row);
  }
  if (!marker) return undefined;
  let row = marker.row - 1;
  while (row >= context.first && context.blank(row)) row--;
  const body: number[] = [];
  while (
    context.available(row) &&
    !context.blank(row) &&
    context.inputsOn(row).length === 0 &&
    !isInstruction(view, row)
  ) {
    body.unshift(row--);
  }
  const headings: number[] = [];
  while (headings.length < 2 && body.length > 0 && isHeadingStyle(view, body[0])) {
    headings.push(body.shift() as number);
  }
  if (body.length < 3) return undefined;
  const wordy = body.filter((r) => segments(view, r).length >= 2);
  if (wordy.length * 10 < body.length * 6) return undefined;
  return {
    kind: 'list',
    option: undefined,
    records: body,
    step: 1,
    lastRow: body[body.length - 1],
    headings,
    filter: undefined,
    legend: [],
    marker,
    ...extent(context, [...headings, ...body], undefined),
  };
}

/** Blue is the colour of instructions ("Type options, press Enter."). */
function isInstruction(view: ScreenView, row: number): boolean {
  return everyCell(view, row, (attribute) => [0x3a, 0x3b, 0x3e].includes(attribute));
}

/** A heading row of a list is in white or reverse image throughout. */
function isHeadingStyle(view: ScreenView, row: number): boolean {
  return everyCell(view, row, (attribute) => isHighIntensity(attribute) || isReverse(attribute));
}

function everyCell(view: ScreenView, row: number, test: (attribute: number) => boolean): boolean {
  const line = view.line(row);
  for (let i = 0; i < line.length; i++) {
    if (line[i] !== ' ' && !test(view.attribute(row, i + 1))) return false;
  }
  return true;
}
