import { describe, expect, it } from 'vitest';
import { decorate } from '../src/decorate/index.ts';
import { recognize } from '../src/recognize/index.ts';
import { screenView } from '../src/recognize/view.ts';
import { recognizeWindows } from '../src/recognize/window.ts';
import { FFW, ic, sba, sf, show, soh, text } from './screens.ts';

/**
 * A window as the host draws one: blue dots and colons, each border character between
 * attribute bytes. `left` and `right` are the border columns, the title is centred in the
 * top border, and the content is `left + 2` to `right - 2`.
 */
function frame(top: number, left: number, right: number, bottom: number, title = ''): number[] {
  const width = right - left + 1;
  const before = Math.floor((width - title.length) / 2);
  const border = '.'.repeat(before) + title + '.'.repeat(width - title.length - before);
  const orders = [...sba(top, left - 1), 0x3a, ...text(border), 0x20];
  for (let row = top + 1; row < bottom; row++) {
    orders.push(...sba(row, left - 1), 0x3a, ...text(':'), 0x20);
    orders.push(...sba(row, right - 1), 0x3a, ...text(':'), 0x20);
  }
  orders.push(...sba(bottom, left - 1), 0x3a, ...text(`:${'.'.repeat(width - 2)}:`), 0x20);
  return orders;
}

/**
 * A window drawn with WDWBORDER((*DSPATR RI) (*CHAR '        ')), as the host sends one:
 * reverse-image blanks along the top and bottom and in each side column, each between
 * attribute bytes. `attribute` 0x3a instead draws blanks that show nothing.
 */
function bars(
  top: number,
  left: number,
  right: number,
  bottom: number,
  attribute = 0x3b,
): number[] {
  const bar = (row: number) => [
    ...sba(row, left - 1),
    attribute,
    ...text(' '.repeat(right - left + 1)),
    0x20,
  ];
  const orders = bar(top);
  for (let row = top + 1; row < bottom; row++) {
    orders.push(...sba(row, left - 1), attribute, ...text(' '), 0x20);
    orders.push(...sba(row, right - 1), attribute, ...text(' '), 0x20);
  }
  orders.push(...bar(bottom));
  return orders;
}

const windows = (orders: (number | number[])[], wide = false) =>
  recognizeWindows(screenView(show(orders, { wide }).screen));

describe('windows', () => {
  it('finds a window with its title in the border', () => {
    const [found] = windows([frame(12, 30, 78, 25, 'Aaaa Bbbb Cccc')], true);
    expect(found.border).toEqual({ row: 12, column: 30, rows: 14, columns: 49 });
    expect(found.content).toEqual({ row: 13, column: 32, rows: 12, columns: 45 });
    expect(found.title).toEqual({ row: 12, column: 47, length: 14 });
    expect(found.titleInBorder).toBe(true);
  });

  it('takes a centred first content row as the title', () => {
    const [found] = windows([frame(8, 14, 69, 18), sba(9, 29), 0x20, text('Aaaa Bbbb Cccc')]);
    expect(found.title).toEqual({ row: 9, column: 30, length: 14 });
    expect(found.titleInBorder).toBe(false);
    const offCentre = windows([frame(8, 14, 69, 18), sba(9, 16), 0x20, text('Aaaa Bbbb')]);
    expect(offCentre[0].title).toBeUndefined();
  });

  it('needs an attribute byte before the dots, colons on every row and a bottom row', () => {
    // Dot leaders in a prompt: no attribute byte just before them.
    const leaders = [sba(5, 2), text('Aaaa ..........'), sba(6, 2), text('Bbbb ..........')];
    expect(windows(leaders)).toEqual([]);
    // A side missing on one row.
    expect(windows([frame(5, 10, 40, 12), sba(8, 40), text(' ')])).toEqual([]);
    // The bottom only one row below the top.
    expect(windows([frame(5, 10, 40, 6)])).toEqual([]);
    // Too few dots.
    expect(windows([frame(5, 10, 14, 9)])).toEqual([]);
  });

  it('finds a window drawn with reverse-image bars, with a centred title', () => {
    const [found] = windows([bars(9, 2, 62, 18), sba(10, 25), 0x23, text(' Aaaa bb ')]);
    expect(found.border).toEqual({ row: 9, column: 2, rows: 10, columns: 61 });
    expect(found.content).toEqual({ row: 10, column: 4, rows: 8, columns: 57 });
    expect(found.title).toEqual({ row: 10, column: 27, length: 7 });
  });

  it('needs reverse image, a bar side on every row and a bar at the bottom', () => {
    // Blanks that show nothing: not recognized.
    expect(windows([bars(9, 2, 62, 18, 0x3a)])).toEqual([]);
    // A side missing on one row.
    expect(windows([bars(9, 2, 62, 18), sba(12, 61), text('  ')])).toEqual([]);
    // Two reverse bars with nothing between them.
    const two = [sba(5, 9), 0x21, text(' '.repeat(20)), 0x20, sba(9, 9), 0x21];
    expect(windows([...two, text(' '.repeat(20)), 0x20])).toEqual([]);
  });

  it('makes the window holding the cursor the active one', () => {
    const screen = show([frame(2, 5, 30, 8), frame(12, 40, 70, 18), ic(14, 45)]);
    expect(recognize(screenView(screen.screen)).window?.border.row).toBe(12);
  });

  it('otherwise makes the one with an input field active', () => {
    const screen = show([
      frame(2, 5, 30, 8),
      frame(12, 40, 70, 18),
      sba(4, 8),
      sf(FFW.alpha, 0x24, 5),
      ic(22, 1),
    ]);
    expect(recognize(screenView(screen.screen)).window?.border.row).toBe(2);
  });
});

/** A screen behind a window: a header, a legend at the bottom and a field. */
const BEHIND = [
  sba(1, 1),
  0x20,
  text('PGMAAA/FMT1'),
  sba(1, 30),
  0x22,
  text('Aaaa Bbbb'),
  sba(5, 2),
  sf(FFW.alpha, 0x24, 5),
  sba(24, 1),
  0x3a,
  text('F3=Exit   F12=Cancel'),
];
/** A window over it, with its own field and legend. */
const WINDOW = [
  frame(8, 20, 60, 18, 'Cccc Dddd'),
  sba(10, 23),
  sf(FFW.alpha, 0x24, 10),
  sba(16, 21),
  0x3a,
  text('F12=Keep   F23=Delete'),
];

describe('scopes', () => {
  it('finds the legend inside the window, and keeps the screen behind inert', () => {
    const recognized = recognize(screenView(show([...BEHIND, ...WINDOW]).screen));
    const [behind, inside] = recognized.scopes;
    expect(behind.inert).toBe(true);
    expect(behind.keys[0].items.map((i) => i.key)).toEqual(['F3', 'F12']);
    expect(behind.header?.id?.text).toBe('PGMAAA/FMT1');
    expect(inside.inert).toBe(false);
    expect(inside.keys[0].items.map((i) => [i.key, i.span.row, i.span.column])).toEqual([
      ['F12', 16, 22],
      ['F23', 16, 33],
    ]);
  });

  it('gives targets only inside the window, and dims everything around it', () => {
    const decorations = decorate(show([...BEHIND, ...WINDOW]).screen);
    expect(decorations.targets.map((t) => t.id)).toEqual(['pill:16:22', 'pill:16:33']);
    const inert = (id: string) => decorations.boxes.find((b) => b.id === id)?.modifiers;
    expect(inert('header')).toContain('inert');
    expect(inert('chip:id')).toContain('inert');
    expect(inert('pill:24:2')).toContain('inert');
    expect(inert('field:5:3')).toContain('inert');
    expect(inert('field:10:24')).not.toContain('inert');
    expect(inert('pill:16:22')).not.toContain('inert');
    expect(decorations.boxes.find((b) => b.kind === 'window')).toMatchObject({
      rect: { row: 8, column: 20, rows: 11, columns: 41 },
      modifiers: ['titled'],
    });
    const styled = (className: string, row: number) =>
      decorations.text
        .filter((t) => t.className === className && t.span.row === row)
        .map((t) => [t.span.column, t.span.length]);
    // The top border around the title, both sides, and the screen beside the window.
    expect(styled('g-border', 8)).toEqual([
      [20, 16],
      [45, 16],
    ]);
    expect(styled('g-window-title', 8)).toEqual([[36, 9]]);
    expect(styled('g-border', 12)).toEqual([
      [20, 1],
      [60, 1],
    ]);
    expect(styled('g-inert', 12)).toEqual([
      [1, 19],
      [61, 20],
    ]);
    expect(styled('g-inert', 24)).toEqual([[1, 80]]);
  });

  it('draws no header when the window covers row 1', () => {
    const recognized = recognize(
      screenView(show([sba(1, 40), text('Aaaa Bbbb'), frame(1, 10, 60, 12), ic(5, 12)]).screen),
    );
    expect(recognized.window?.border.row).toBe(1);
    expect(recognized.scopes.map((s) => s.header)).toEqual([undefined, undefined]);
  });

  it('takes a message written to the window, banded to its content columns', () => {
    const screen = show([soh(17), ...BEHIND, ...WINDOW]);
    // Write Error Code to Window, columns 22 to 58.
    screen.hostError('Aaaa is not valid.', { window: [22, 58] });
    expect(recognize(screenView(screen.screen)).message).toEqual({
      row: 17,
      column: 23,
      length: 18,
      kind: 'error',
      more: false,
      band: { row: 17, column: 22, length: 37 },
    });
    const info = show([soh(17), ...BEHIND, ...WINDOW, sba(17, 22), 0x22, text('Aaaa saved.')]);
    expect(recognize(screenView(info.screen)).message).toMatchObject({ kind: 'info', column: 23 });
  });

  it("reads the screen's error row when the window has no message line", () => {
    const screen = show([...WINDOW]);
    screen.hostError('Aaaa is not valid.');
    const recognized = recognize(screenView(screen.screen));
    expect(recognized.message).toMatchObject({
      row: 24,
      column: 2,
      band: { row: 24, column: 1, length: 80 },
    });
    const { text: styles } = decorate(screen.screen);
    expect(styles.some((t) => t.className === 'g-message' && t.span.row === 24)).toBe(true);
  });

  it('never reads the window border as a message', () => {
    // The error row is the bottom border, and the host is in its error state.
    const screen = show([soh(18), ...BEHIND, ...WINDOW]);
    const snapshot = screen.hostError('', { attribute: null, window: [70, 79] }).screen;
    expect(snapshot.error?.kind).toBe('host');
    expect(recognize(screenView(snapshot)).message).toBeUndefined();
  });
});
