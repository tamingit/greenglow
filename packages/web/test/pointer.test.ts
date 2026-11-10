import { describe, expect, it } from 'vitest';
import { decorate } from '../src/decorate/index.ts';
import { bandAt, SwipePager, WheelPager } from '../src/pointer.ts';
import { FFW, sba, sf, show, text } from './screens.ts';

const record = (row: number) => [
  sba(row, 1),
  sf(FFW.alpha, 0x24, 1),
  sba(row, 3),
  0x20,
  text('Aaaa Bbbb'),
];

const screen = decorate(
  show(
    [
      [sba(7, 1), 0x22, text('Opt  Name')],
      [8, 9, 10].map(record),
      [sba(22, 70), 0x22, text('More...')],
    ].flat(Infinity) as number[],
  ).screen,
);

describe('finding the record under the pointer', () => {
  it('finds the hover band of the record under the pointer, and none off a record', () => {
    expect(bandAt(screen, { row: 9, column: 5 })).toBe('band:9');
    expect(bandAt(screen, { row: 7, column: 3 })).toBeUndefined();
    expect(bandAt(screen, { row: 14, column: 3 })).toBeUndefined();
    expect(bandAt(undefined, { row: 9, column: 5 })).toBeUndefined();
  });

  it('ignores a table that is inert', () => {
    const [table] = screen.tables;
    const inert = { ...screen, tables: [{ ...table, inert: true }] };
    expect(bandAt(inert, { row: 9, column: 3 })).toBeUndefined();
  });
});

describe('the wheel pager', () => {
  it('rolls down for any turn down and up for any turn up', () => {
    expect(new WheelPager().wheel(1000, 100, true)).toBe('RollUp');
    expect(new WheelPager().wheel(1000, -100, true)).toBe('RollDown');
    expect(new WheelPager().wheel(1000, 1, true)).toBe('RollUp');
    expect(new WheelPager().wheel(1000, -0.5, true)).toBe('RollDown');
  });

  it('rolls on every event once the keyboard is free, so spinning keeps paging', () => {
    const pager = new WheelPager(80);
    expect(pager.wheel(1000, 100, true)).toBe('RollUp');
    expect(pager.wheel(1100, 100, true)).toBe('RollUp');
    expect(pager.wheel(1200, -100, true)).toBe('RollDown');
  });

  it('never rolls while the keyboard is locked, and does not remember those events', () => {
    const pager = new WheelPager(80);
    expect(pager.wheel(1000, 100, true)).toBe('RollUp');
    expect(pager.wheel(1030, 100, false)).toBeUndefined();
    expect(pager.wheel(1060, 100, false)).toBeUndefined();
    // The host answered: the very next event rolls.
    expect(pager.wheel(1090, 100, true)).toBe('RollUp');
  });

  it('keeps a minimum gap between rolls, so a fast host cannot be flooded', () => {
    const pager = new WheelPager(80);
    const rolls = [0, 16, 33, 50, 66, 83, 100, 116, 133, 150, 166].map((t) =>
      pager.wheel(1000 + t, 100, true),
    );
    expect(rolls.filter(Boolean)).toHaveLength(3);
    expect(rolls[0]).toBe('RollUp');
    expect(rolls[5]).toBe('RollUp');
  });

  it('never rolls for a wheel event with no vertical movement', () => {
    expect(new WheelPager().wheel(1000, 0, true)).toBeUndefined();
  });
});

describe('swiping on a touch screen', () => {
  it('pages once the finger has moved far enough, mostly up or down', () => {
    const swipe = new SwipePager(48);
    swipe.start(100, 300);
    expect(swipe.move(100, 270, true)).toBeUndefined();
    // Up the screen: the list moves up, Page Down.
    expect(swipe.move(100, 250, true)).toBe('RollUp');
    // Keeps going: another page after as far again.
    expect(swipe.move(100, 220, true)).toBeUndefined();
    expect(swipe.move(100, 200, true)).toBe('RollUp');
    swipe.end();
    expect(swipe.move(100, 100, true)).toBeUndefined();
  });

  it('pages back for a swipe down, and not for one that is mostly sideways', () => {
    const swipe = new SwipePager(48);
    swipe.start(100, 100);
    expect(swipe.move(100, 160, true)).toBe('RollDown');
    swipe.start(100, 100);
    expect(swipe.move(250, 160, true)).toBeUndefined();
  });

  it('drops a swipe while the keyboard is locked, never typing it ahead', () => {
    const swipe = new SwipePager(48);
    swipe.start(100, 300);
    expect(swipe.move(100, 240, false)).toBeUndefined();
    expect(swipe.move(100, 230, true)).toBeUndefined();
  });
});
