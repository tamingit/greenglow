import { describe, expect, it } from 'vitest';
import { readTable, toTsv } from '../src/copytable.ts';
import { FFW, sba, sf, show as showOrders, text } from './screens.ts';

type Orders = unknown[];

const show = (orders: Orders) => showOrders(orders.flat(Infinity) as number[]);

/** A record: an option field in column 2, then its columns. */
const record = (row: number, data: string, option = ''): Orders => [
  sba(row, 1),
  sf(FFW.alpha, 0x24, 1),
  ...(option ? [text(option)] : []),
  sba(row, 3),
  0x20,
  // Under the heading's columns: its attribute byte is in column 1, its text from column 2.
  sba(row, 7),
  text(data),
];

const page = (rows: string[], marker: string, options: string[] = []): Orders => [
  sba(4, 2),
  0x3a,
  text('2=Change  4=Delete'),
  sba(6, 1),
  0x22,
  text('Opt  Item       Description               Amount'),
  0x20,
  ...rows.map((data, i) => record(7 + i, data, options[i])),
  sba(7 + rows.length, 70),
  0x22,
  text(marker),
];

const ROWS = [
  'ITEM001    Placeholder item 1           3.00 ',
  'ITEM002    Placeholder item 2          12.50-',
  'ITEM003    Placeholder item 3         100.00 ',
];

describe('reading a table', () => {
  it('splits headings and records into columns and leaves out the option field', () => {
    const table = readTable(show(page(ROWS, 'More...', ['4', '', '2'])).screen)!;
    expect(table.headings).toEqual([['Item', 'Description', 'Amount']]);
    expect(table.records).toEqual([
      ['ITEM001', 'Placeholder item 1', '3.00'],
      ['ITEM002', 'Placeholder item 2', '12.50-'],
      ['ITEM003', 'Placeholder item 3', '100.00'],
    ]);
    expect(table.more).toBe(true);
  });

  it('knows the last page, and tells pages apart', () => {
    const last = readTable(show(page(ROWS.slice(0, 2), 'Bottom')).screen)!;
    expect(last.more).toBe(false);
    expect(last.marked).toBe(true);
    const first = readTable(show(page(ROWS, 'More...')).screen)!;
    expect(last.key).not.toBe(first.key);
  });

  it('may have more to roll to when the screen shows no marker', () => {
    const table = readTable(show(page(ROWS, '')).screen)!;
    expect(table.records).toHaveLength(3);
    expect(table.more).toBe(true);
    expect(table.marked).toBe(false);
  });

  it('finds nothing on a screen without a table', () => {
    expect(readTable(show([sba(1, 2), text('Placeholder')]).screen)).toBeUndefined();
  });
});

describe('tab-separated text', () => {
  it('is a line per row and defuses formulas but not negative amounts', () => {
    expect(
      toTsv(
        [['A', 'B']],
        [
          ['=SUM(1)', '-12.50'],
          ['x', '12.50-'],
        ],
      ),
    ).toBe("A\tB\r\n'=SUM(1)\t-12.50\r\nx\t12.50-\r\n");
  });
});
