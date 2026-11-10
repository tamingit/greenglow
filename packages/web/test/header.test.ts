import { describe, expect, it } from 'vitest';
import { recognizeHeader } from '../src/recognize/header.ts';
import { screenView } from '../src/recognize/view.ts';
import { FFW, sba, sf, show, text } from './screens.ts';

const header = (orders: (number | number[])[], wide = false) =>
  recognizeHeader(screenView(show(orders, { wide }).screen), new Set());

describe('header', () => {
  it('finds the program ID and a high-intensity title', () => {
    const found = header([
      sba(1, 1),
      0x20,
      text('PGMAAA/FMT1'),
      sba(1, 18),
      0x22,
      text('Aaaa Bbbb'),
    ]);
    expect(found?.id).toEqual({ span: { row: 1, column: 2, length: 11 }, text: 'PGMAAA/FMT1' });
    expect(found?.title).toEqual({ row: 1, column: 19, length: 9 });
  });

  it('accepts IDs with a digit or a camel hump, and capitals beside other text', () => {
    expect(header([sba(1, 2), text('CamelId    New Thing')])?.id?.text).toBe('CamelId');
    expect(header([sba(1, 2), text('abc123  Thing')])?.id?.text).toBe('abc123');
    expect(header([sba(1, 2), text('MAIN'), sba(1, 30), text('Aaaa Menu')])?.id?.text).toBe('MAIN');
  });

  it('takes no ID from long names, lone capitals, words or prompts', () => {
    expect(header([sba(1, 2), text('ABCDEFGHIJK'), sba(1, 50), text('Aaa: 1')])?.id).toBe(
      undefined,
    );
    expect(header([sba(1, 2), text('ALLCAPS')])?.id).toBeUndefined();
    expect(header([sba(1, 2), text('Select    Aaaa Bbbb')])?.id).toBeUndefined();
    expect(header([sba(1, 2), text('Columns . . . :   6 100')])?.id).toBeUndefined();
  });

  it('takes a centred title when nothing is in high intensity', () => {
    expect(header([sba(1, 34), text('Aaaa Bbbb')])?.title).toEqual({
      row: 1,
      column: 34,
      length: 9,
    });
  });

  it('draws no header on a blank row 1 or one with an input field', () => {
    expect(header([sba(5, 2), text('Something')])).toBeUndefined();
    expect(header([sba(1, 2), text('Name'), sba(1, 10), sf(FFW.alpha, 0x24, 5)])).toBeUndefined();
  });
});
