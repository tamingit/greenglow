import { describe, expect, it } from 'vitest';
import { decorate } from '../src/decorate/index.ts';
import { recognize } from '../src/recognize/index.ts';
import { findAmounts } from '../src/recognize/numbers.ts';
import { recognizeSubfiles } from '../src/recognize/subfile.ts';
import { screenView } from '../src/recognize/view.ts';
import { FFW, sba, sf, show as showOrders, text } from './screens.ts';

type Orders = unknown[];

/** The screen for nested lists of orders. */
const show = (orders: Orders, options: { wide?: boolean } = {}) =>
  showOrders(orders.flat(Infinity) as number[], options);

/** A record: an option field at `column`, then the record's data after one more cell. */
function record(row: number, data: string, column = 2, length = 1): Orders {
  return [
    sba(row, column - 1),
    sf(FFW.alpha, 0x24, length),
    sba(row, column + length),
    0x20,
    text(data),
  ];
}

const records = (rows: number[], data = 'Aaaa Bbbb   1,234.50', column = 2, length = 1) =>
  rows.map((row) => record(row, data, column, length));

/** A heading row in high intensity, with two or more segments. */
const heading = (row: number, words = 'Opt  Name       Amount'): Orders => [
  sba(row, 1),
  0x22,
  text(words),
];

/** A legend of option codes in blue. */
const legend = (row: number, words = '2=Edit  4=Delete'): Orders => [
  sba(row, 1),
  0x3a,
  text(words),
];

const marker = (row: number, word = 'More...', column = 70): Orders => [
  sba(row, column),
  0x22,
  text(word),
];

const found = (orders: Orders, wide = false) =>
  recognizeSubfiles(screenView(show(orders, { wide }).screen), new Set());

const TABLE: Orders = [legend(5), heading(7), records([8, 9, 10, 11]), marker(22)];

describe('option lists', () => {
  it('finds the option column, headings, legend and marker', () => {
    const [list] = found(TABLE);
    expect(list.kind).toBe('options');
    expect(list.option).toEqual({ column: 2, length: 1 });
    expect(list.records).toEqual([8, 9, 10, 11]);
    expect(list.step).toBe(1);
    expect(list.lastRow).toBe(11);
    expect(list.headings).toEqual([7]);
    expect(list.legend.map((l) => [l.row, l.items.map((i) => i.code)])).toEqual([[5, ['2', '4']]]);
    expect(list.marker).toMatchObject({ row: 22, text: 'More...', more: true });
    expect(list.marker?.span).toEqual({ row: 22, column: 71, length: 7 });
    expect(list.left).toBe(1);
  });

  it('claims the rows it takes', () => {
    const claimed = new Set<number>();
    recognizeSubfiles(screenView(show(TABLE).screen), claimed);
    expect([...claimed].sort((a, b) => a - b)).toEqual([5, 7, 8, 9, 10, 11, 22]);
  });

  it('takes two heading rows, and a filter row with a blank row before the records', () => {
    const [list] = found([
      heading(4, 'Customer#  Name        City'),
      heading(5, 'Account    Sts          State'),
      [sba(6, 4), sf(FFW.alpha, 0x24, 8), sba(6, 20), sf(FFW.alpha, 0x24, 12)],
      records([8, 9, 10]),
    ]);
    expect(list.headings).toEqual([4, 5]);
    expect(list.filter?.row).toBe(6);
    expect(list.filter?.fields.map((f) => f.column)).toEqual([5, 21]);
  });

  it('takes no more than two heading rows, and none above a gap with no filter row', () => {
    const [three] = found([
      heading(5, 'Aaaa  Bbbb'),
      heading(6, 'Cccc  Dddd'),
      heading(7, 'Eeee  Ffff'),
      records([8, 9]),
    ]);
    expect(three.headings).toEqual([6, 7]);
    const [gap] = found([heading(5, 'Aaaa  Bbbb'), records([7, 8, 9])]);
    expect(gap.headings).toEqual([]);
  });

  it('does not take a legend or a one-segment row as a heading', () => {
    const [list] = found([
      legend(6, 'Options: 5=Select'),
      text('x'),
      records([8, 9]),
      sba(7, 1),
      0x22,
      text('Customer Name'),
    ]);
    expect(list.headings).toEqual([]);
    expect(list.legend.map((l) => l.row)).toEqual([6]);
  });

  it('reads a legend with a leading word, and keeps codes no longer than the field', () => {
    const [list] = found([legend(5, 'Option 5=Select'), heading(7), records([8, 9, 10])]);
    expect(list.legend.map((l) => l.items.map((i) => i.label))).toEqual([['Select']]);
    // Codes of two characters do not fit a one-character field.
    const [tooLong] = found([legend(5, '13=Change text'), heading(7), records([8, 9, 10])]);
    expect(tooLong.legend).toEqual([]);
    const [fits] = found([
      legend(5, '13=Change text  2=Edit'),
      heading(7),
      records([8, 9, 10], 'Aaaa', 2, 2),
    ]);
    expect(fits.legend[0].items.map((i) => i.code)).toEqual(['13', '2']);
  });

  it('finds a legend a few rows above, past other rows', () => {
    const [list] = found([
      legend(2, '1=Select  M=Master'),
      [sba(3, 3), text('Some text')],
      heading(6),
      records([7, 8, 9]),
    ]);
    expect(list.legend.map((l) => l.row)).toEqual([2]);
  });

  it('finds the marker below blank rows, as Bottom or a plus, and only if nothing else is there', () => {
    const marked = (word: string) => found([records([8, 9, 10]), marker(20, word)])[0].marker;
    expect(marked('Bottom')).toMatchObject({ text: 'Bottom', more: false });
    expect(marked('+')).toMatchObject({ text: '+', more: true });
    expect(marked('More...')?.row).toBe(20);
    const stray = found([records([8, 9, 10]), [sba(12, 3), text('Total 12')], marker(20)])[0];
    expect(stray.marker).toBeUndefined();
    expect(found([records([8, 9, 10]), marker(20, 'More here')])[0].marker).toBeUndefined();
  });

  it('allows records of two or three rows, with the option field on the first', () => {
    const [two] = found([records([6, 8, 10, 12]), heading(5)]);
    expect([two.step, two.records, two.lastRow]).toEqual([2, [6, 8, 10, 12], 13]);
    const [three] = found([records([6, 9, 12])]);
    expect([three.step, three.lastRow]).toEqual([3, 14]);
    expect(found([records([6, 8])])).toEqual([]);
  });

  it('needs at least two records at a constant step of up to three rows', () => {
    expect(found([records([8])])).toEqual([]);
    // An irregular row ends the run.
    expect(found([records([8, 9, 11])]).map((l) => l.records)).toEqual([[8, 9]]);
    expect(found([records([8, 13])])).toEqual([]);
  });

  it('needs the fields to share a column and a length of three or fewer', () => {
    expect(found([record(8, 'Aaaa'), record(9, 'Aaaa', 4)])).toEqual([]);
    expect(found([record(8, 'Aaaa'), record(9, 'Aaaa', 2, 2)])).toEqual([]);
    expect(found([records([8, 9, 10], 'Aaaa', 2, 4)])).toEqual([]);
    expect(found([records([8, 9, 10], 'Aaaa', 2, 3)])).toHaveLength(1);
  });

  it('needs the left side blank: no text and no other field', () => {
    // Labels to the left, as in a prompt of Y/N fields.
    const prompts = [8, 9, 10].map((row) => [
      sba(row, 2),
      text('Print the report'),
      ...record(row, 'Yes', 30, 1),
    ]);
    expect(found(prompts)).toEqual([]);
    // Another field to the left, as in an order-entry grid.
    const grid = [8, 9, 10].map((row) => [
      sba(row, 1),
      sf(FFW.bypass, 0x20, 4),
      ...record(row, 'Aaaa', 20, 2),
    ]);
    expect(found(grid)).toEqual([]);
  });

  it('needs data to the right of at least half the records', () => {
    expect(found([records([8, 9, 10], '')])).toEqual([]);
    expect(found([record(8, 'Aaaa'), record(9, ''), record(10, 'Aaaa')])).toHaveLength(1);
    expect(found([record(8, 'Aaaa'), record(9, ''), record(10, '')])).toEqual([]);
  });

  it('takes the leftmost column of two', () => {
    const rows = [8, 9, 10];
    const both = [rows.map((r) => record(r, 'Aaaa')), rows.map((r) => record(r, 'Aaaa', 40))];
    const lists = found(both);
    expect(lists).toHaveLength(1);
    expect(lists[0].option?.column).toBe(2);
  });

  it('works at 27 x 132, and inside a window as its own scope', () => {
    const [wide] = found([heading(7), records([8, 9, 10]), marker(26, 'Bottom', 120)], true);
    expect(wide.marker?.row).toBe(26);
  });
});

describe('display-only lists', () => {
  const row = (r: number, words: string, attribute = 0x20): Orders => [
    sba(r, 1),
    attribute,
    text(words),
  ];
  const LIST: Orders = [
    row(4, 'Type a choice, press Enter.', 0x3a),
    row(5, 'Name      Status      Count', 0x22),
    row(6, 'Aaaa      Active      12'),
    row(7, 'Bbbb      Held        7'),
    row(8, 'Cccc      Active      3'),
    marker(20, 'Bottom'),
  ];

  it('finds rows of columns above a marker, stopping at an instruction row', () => {
    const [list] = found(LIST);
    expect(list.kind).toBe('list');
    expect(list.option).toBeUndefined();
    expect(list.headings).toEqual([5]);
    expect(list.records).toEqual([6, 7, 8]);
    expect(list.marker?.row).toBe(20);
  });

  it('needs a marker, three rows and mostly columns of text', () => {
    expect(found(LIST.slice(0, -1))).toEqual([]);
    expect(found([...LIST.slice(0, 4), marker(20)])).toEqual([]);
    const prose = [4, 5, 6, 7].map((r) => row(r, `This is a line of help text number ${r}`));
    expect(found([...prose, marker(20, 'More...')])).toEqual([]);
  });

  it('is not a list when its rows hold input fields', () => {
    const inputs = [...LIST.slice(0, 4), [sba(8, 20), sf(FFW.alpha, 0x24, 5)], marker(20)];
    expect(found(inputs)).toEqual([]);
  });

  it('gives way to an option list on the same screen', () => {
    expect(found([...LIST, records([12, 13, 14])]).map((l) => l.kind)).toEqual(['options']);
  });
});

/** A window drawn with dots and colons, as the host does. */
function frame(top: number, left: number, right: number, bottom: number): Orders {
  const width = right - left + 1;
  const orders: Orders = [sba(top, left - 1), 0x3a, text('.'.repeat(width)), 0x20];
  for (let row = top + 1; row < bottom; row++) {
    orders.push(sba(row, left - 1), 0x3a, text(':'), 0x20);
    orders.push(sba(row, right - 1), 0x3a, text(':'), 0x20);
  }
  orders.push(sba(bottom, left - 1), 0x3a, text(`:${'.'.repeat(width - 2)}:`), 0x20);
  return orders;
}

describe('tables and windows', () => {
  const inWindow: Orders = [
    frame(8, 20, 70, 20),
    heading(11, ''),
    [sba(11, 22), 0x22, text('Opt  Name      Amount')],
    ...records([13, 14, 15], 'Aaaa Bbbb', 23),
    [sba(19, 60), 0x22, text('More...')],
  ];

  it('recognizes a table inside a window, and it is live', () => {
    const view = screenView(show(inWindow).screen);
    const recognized = recognize(view);
    expect(recognized.window).toBeDefined();
    const [outside, inside] = recognized.scopes;
    expect(outside.subfiles).toEqual([]);
    expect(inside.subfiles).toHaveLength(1);
    expect(inside.subfiles[0].option).toEqual({ column: 23, length: 1 });
    expect(inside.subfiles[0].marker?.row).toBe(19);
    const decorations = decorate(show(inWindow).screen);
    expect(decorations.tables).toHaveLength(1);
    expect(decorations.tables[0].inert).toBe(false);
    expect(decorations.targets.map((t) => t.id)).toContain('pager:19');
  });

  it('draws a table behind an active window inert, with no button, band or wheel', () => {
    const behind: Orders = [
      heading(2, 'Opt  Name      Amount'),
      records([3, 4, 5, 6], 'Aaaa Bbbb'),
      marker(8, 'More...', 10),
      frame(12, 30, 70, 22),
      [sba(15, 34), text('Prompt'), sba(15, 44), sf(FFW.alpha, 0x24, 10)],
    ];
    const decorations = decorate(show(behind).screen);
    expect(decorations.tables).toHaveLength(1);
    expect(decorations.tables[0].inert).toBe(true);
    expect(decorations.targets.filter((t) => t.id.startsWith('pager'))).toEqual([]);
    expect(decorations.boxes.filter((b) => b.kind === 'band')).toEqual([]);
    const table = decorations.boxes.find((b) => b.kind === 'table');
    expect(table?.modifiers).toContain('inert');
    expect(decorations.boxes.find((b) => b.kind === 'pager')?.modifiers).toContain('inert');
  });
});

describe('table decorations', () => {
  const decorations = () => decorate(show([...TABLE]).screen);

  it('draws stripes, a hover band per record, a heading band and the pager', () => {
    const { boxes } = decorations();
    const table = boxes.find((b) => b.kind === 'table');
    expect(table).toMatchObject({ id: 'table:8', rect: { row: 8, rows: 4, column: 1 } });
    expect(table?.modifiers).toEqual([]);
    expect(boxes.filter((b) => b.kind === 'band').map((b) => b.id)).toEqual([
      'band:8',
      'band:9',
      'band:10',
      'band:11',
    ]);
    expect(boxes.find((b) => b.kind === 'heading')?.rect).toMatchObject({ row: 7, rows: 1 });
    expect(boxes.find((b) => b.kind === 'pager')?.modifiers).toEqual(['more']);
  });

  it('gives records of several rows a stripe step and bands of that height', () => {
    const { boxes, tables } = decorate(show([records([6, 8, 10, 12])]).screen);
    expect(boxes.find((b) => b.kind === 'table')?.modifiers).toEqual(['step2']);
    expect(boxes.filter((b) => b.kind === 'band').map((b) => b.rect.rows)).toEqual([2, 2, 2, 2]);
    expect(tables[0].records.map((r) => r.row)).toEqual([6, 8, 10, 12]);
  });

  it('turns More... into a Page Down button and Bottom into Page Up', () => {
    const more = decorations().targets.find((t) => t.id === 'pager:22');
    expect(more?.action).toMatchObject({ type: 'key', key: 'RollUp' });
    expect(more?.label).toBe('Show more (Page Down)');
    const bottom = decorate(show([records([8, 9, 10]), marker(22, 'Bottom')]).screen).targets;
    expect(bottom.find((t) => t.id === 'pager:22')?.action).toMatchObject({
      key: 'RollDown',
      guard: { text: 'Bottom' },
    });
  });

  it('has no button without a marker', () => {
    const none = decorate(show([records([8, 9, 10])]).screen);
    expect(none.targets).toEqual([]);
    expect(none.tables).toHaveLength(1);
  });

  it('styles filter-row fields as search boxes, with a magnifier in the option column', () => {
    const { boxes } = decorate(
      show([
        heading(5, 'Customer#  Name'),
        [sba(6, 4), sf(FFW.alpha, 0x24, 8), sba(6, 20), sf(FFW.alpha, 0x24, 12)],
        records([8, 9, 10]),
      ]).screen,
    );
    const fields = boxes.filter((b) => b.kind === 'field' && b.rect.row === 6);
    expect(fields.map((b) => b.modifiers.includes('search'))).toEqual([true, true]);
    expect(boxes.find((b) => b.kind === 'search')?.rect).toEqual({
      row: 6,
      column: 2,
      rows: 1,
      columns: 1,
    });
    expect(boxes.find((b) => b.id === 'heading:5')?.modifiers).toEqual(['filtered']);
    // The option fields are not search boxes.
    expect(boxes.find((b) => b.id === 'field:8:2')?.modifiers).not.toContain('search');
  });

  it('marks amounts, and credits, outside input fields', () => {
    const { text: styles } = decorate(
      show([
        record(8, 'Aaaa   312.40-   1,234.50'),
        record(9, 'Bbbb   -45.00  19820567  7/17/2026'),
        record(10, 'Cccc'),
      ]).screen,
    );
    const classes = (row: number) =>
      styles
        .filter((s) => s.span.row === row && s.className.startsWith('g-num'))
        .map((s) => [s.span.column, s.span.length, s.className]);
    expect(classes(8)).toEqual([
      [11, 7, 'g-num g-credit'],
      [21, 8, 'g-num'],
    ]);
    expect(classes(9)).toEqual([[11, 6, 'g-num g-credit']]);
  });
});

describe('amounts', () => {
  it('needs a decimal point or digit groups, and leaves identifiers and dates alone', () => {
    const amounts = (line: string) => findAmounts(line).map((a) => [a.column, a.length, a.credit]);
    expect(amounts('  8,461.10 ')).toEqual([[3, 8, false]]);
    expect(amounts('312.40-')).toEqual([[1, 7, true]]);
    expect(amounts('-1,200')).toEqual([[1, 6, true]]);
    expect(amounts('19820567 7/17/2026 10:39:19 2144845212 A-12 v1.2.3 4-5')).toEqual([]);
  });
});
