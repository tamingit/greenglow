import { encodeEbcdic } from '../src/protocol/index.ts';
import { describe, expect, it } from 'vitest';
import { ATTRIBUTE_CLASSES, rowRuns, rowStyles } from '../src/text.ts';

/** A row from text and the attribute in effect for each cell. */
function row(text: string, attributes: number[]): [Uint8Array, Uint8Array] {
  return [encodeEbcdic(text), Uint8Array.from(attributes)];
}

describe('Classic text runs', () => {
  it('pins the colour and highlight classes for every attribute (table 15-10)', () => {
    expect(ATTRIBUTE_CLASSES).toHaveLength(32);
    expect(ATTRIBUTE_CLASSES[0x20 - 0x20]).toBe('c-green');
    expect(ATTRIBUTE_CLASSES[0x22 - 0x20]).toBe('c-white');
    expect(ATTRIBUTE_CLASSES[0x24 - 0x20]).toBe('c-green ul');
    expect(ATTRIBUTE_CLASSES[0x28 - 0x20]).toBe('c-red');
    expect(ATTRIBUTE_CLASSES[0x2a - 0x20]).toBe('c-red blink');
    expect(ATTRIBUTE_CLASSES[0x30 - 0x20]).toBe('c-turquoise cs');
    expect(ATTRIBUTE_CLASSES[0x3a - 0x20]).toBe('c-blue');
    for (const nondisplay of [0x27, 0x2f, 0x37, 0x3f]) {
      expect(ATTRIBUTE_CLASSES[nondisplay - 0x20]).toBe('nd');
    }
  });

  it('merges cells with the same look and blanks attribute cells', () => {
    const [bytes, attributes] = row('AB CD', [0x20, 0x20, 0x22, 0x22, 0x22]);
    bytes[2] = 0x22; // an attribute byte takes a cell
    expect(rowRuns(bytes, attributes)).toEqual([
      { className: 'c-green', text: 'AB' },
      { className: 'attribute', text: ' ' },
      { className: 'c-white', text: 'CD' },
    ]);
  });

  it('hides nondisplay text and shows nulls, Dup and other low bytes as ACS does', () => {
    const [bytes, attributes] = row('PASS', [0x27, 0x27, 0x27, 0x27]);
    expect(rowRuns(bytes, attributes)).toEqual([{ className: 'nd', text: '    ' }]);
    const low = Uint8Array.from([0x00, 0x1c, 0x1f]);
    expect(rowRuns(low, Uint8Array.from([0x20, 0x20, 0x20]))).toEqual([
      { className: 'c-green', text: ' *█' },
    ]);
  });
});

describe('look styles on the text', () => {
  it('adds classes per cell, splitting runs where they start and end', () => {
    const styles = rowStyles(
      [{ span: { row: 1, column: 2, length: 2 }, className: 'g-input' }],
      [],
      1,
      5,
    );
    const [bytes, attributes] = row('ABCDE', [0x20, 0x20, 0x20, 0x20, 0x20]);
    expect(rowRuns(bytes, attributes, styles[0])).toEqual([
      { className: 'c-green', text: 'A' },
      { className: 'c-green g-input', text: 'BC' },
      { className: 'c-green', text: 'DE' },
    ]);
  });

  it('shows masked cells as bullets where something is typed, blanks elsewhere', () => {
    const styles = rowStyles([], [{ row: 1, column: 1, length: 4 }], 1, 4);
    const bytes = Uint8Array.from([0xc1, 0x00, 0x40, 0xf1]);
    expect(rowRuns(bytes, Uint8Array.from([0x27, 0x27, 0x27, 0x27]), styles[0])).toEqual([
      { className: 'nd', text: '•  •' },
    ]);
  });
});
