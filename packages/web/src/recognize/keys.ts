// Function-key legends near the bottom of the screen, such as "F3=Exit   F12=Cancel". Each
// item becomes a button that presses the key the screen names. A row counts only when
// every word on it belongs to a legend item, so data that happens to contain "F3=" stays
// plain.

import type { AidKeyName } from '../protocol/index.ts';
import type { Span } from '../decorations.ts';
import { KEY_CODES, tokenize } from './codes.ts';
import { fieldCells, type ScreenView } from './view.ts';

export interface KeyItem {
  readonly span: Span;
  /** The code as the screen shows it ("F3", "Enter") and the cells it takes. */
  readonly code: string;
  readonly codeLength: number;
  /** Empty for a bare roll key, which names its key and has no label. */
  readonly label: string;
  readonly key: AidKeyName;
}

export interface KeyLegend {
  readonly row: number;
  readonly items: readonly KeyItem[];
}

/**
 * The rows legends sit on: the screen's last five (rows 20–24 on 24 x 80, 23–27 on
 * 27 x 132), or a window's last four content rows.
 */
const SCREEN_ROWS = 5;
const WINDOW_ROWS = 4;

export function recognizeKeys(view: ScreenView, claimed: Set<number>): KeyLegend[] {
  const legends: KeyLegend[] = [];
  const cells = fieldCells(view);
  const { bounds } = view;
  const last = view.scope === 'window' ? bounds.row + bounds.rows - 1 : view.rows;
  const first =
    view.scope === 'window'
      ? Math.max(bounds.row, last - WINDOW_ROWS + 1)
      : Math.max(2, last - SCREEN_ROWS + 1);
  for (let row = first; row <= last; row++) {
    if (claimed.has(row)) continue;
    const legend = legendOn(view, row, cells);
    if (!legend) continue;
    legends.push(legend);
    claimed.add(row);
  }
  return legends;
}

/**
 * A roll key printed without a label, as in "F3=Exit  F16=Reposition  Rolldown". It names
 * its own key, so it is an item by itself. Other leftover words still reject the row.
 */
const BARE_ROLL =
  /(?<![^\s,;|])(?:Rollup|RollUp|ROLLUP|Rolldown|RollDown|ROLLDOWN|PageUp|Pageup|PAGEUP|PageDown|Pagedown|PAGEDOWN|PgUp|PgDn)(?![^\s,;|])/g;

function legendOn(view: ScreenView, row: number, cells: Set<number>): KeyLegend | undefined {
  const { items, rest: unexplained } = tokenize(view.line(row), KEY_CODES, { dash: true });
  if (items.length === 0) return undefined;
  const bare = Array.from(unexplained.matchAll(BARE_ROLL), (match) => ({
    code: match[0],
    label: '',
    column: match.index + 1,
    end: match.index + 1 + match[0].length,
  }));
  const rest = unexplained.replace(BARE_ROLL, (word) => ' '.repeat(word.length));
  if (/[^\s,;|]/.test(rest)) return undefined;
  const legendItems: KeyItem[] = [];
  for (const item of [...items, ...bare].sort((a, b) => a.column - b.column)) {
    const key = aidKey(item.code);
    if (!key || (item.label === '' && !bare.includes(item))) return undefined;
    const span = { row, column: item.column, length: item.end - item.column };
    // Text inside a field is data (SEU source, message text), not a legend.
    for (let c = span.column; c < span.column + span.length; c++) {
      if (cells.has((row - 1) * view.columns + c - 1)) return undefined;
    }
    legendItems.push({
      span,
      code: item.code,
      codeLength: item.code.length,
      label: item.label,
      key,
    });
  }
  return { row, items: legendItems };
}

/** The AID a legend code names. */
export function aidKey(code: string): AidKeyName | undefined {
  const fn = /^F(\d{1,2})$/.exec(code);
  if (fn) {
    const n = Number(fn[1]);
    return n >= 1 && n <= 24 ? (`F${n}` as AidKeyName) : undefined;
  }
  switch (code.toLowerCase()) {
    case 'enter':
      return 'Enter';
    case 'pagedown':
    case 'pgdn':
    case 'rollup':
      return 'RollUp';
    case 'pageup':
    case 'pgup':
    case 'rolldown':
      return 'RollDown';
    case 'help':
      return 'Help';
    case 'print':
      return 'Print';
    default:
      return undefined;
  }
}
