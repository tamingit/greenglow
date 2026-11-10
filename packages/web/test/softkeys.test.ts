import { describe, expect, it } from 'vitest';
import { SENTINEL, inInputField, softKeys } from '../src/softkeys.ts';
import { FFW, sba, sf, show, text } from './screens.ts';

describe('the on-screen keyboard', () => {
  it('sends what was typed after the sentinel, one key a character', () => {
    expect(softKeys(`${SENTINEL}ab`)).toEqual([
      { type: 'character', char: 'a' },
      { type: 'character', char: 'b' },
    ]);
    expect(softKeys(SENTINEL)).toEqual([]);
  });

  it('turns a line break into Enter', () => {
    expect(softKeys(`${SENTINEL}x\n`)).toEqual([
      { type: 'character', char: 'x' },
      { type: 'function', name: 'Enter' },
    ]);
  });

  it('turns deleting the sentinel into Backspace', () => {
    expect(softKeys('')).toEqual([{ type: 'function', name: 'Backspace' }]);
    // A correction that replaced it: Backspace, then the new text.
    expect(softKeys('y')).toEqual([
      { type: 'function', name: 'Backspace' },
      { type: 'character', char: 'y' },
    ]);
  });

  it('opens only on an input field', () => {
    const { screen } = show([
      sba(3, 2),
      text('Amount'),
      sba(3, 20),
      sf(FFW.numeric, 0x24, 5),
      sba(5, 20),
      sf(FFW.bypass, 0x20, 5),
    ]);
    expect(inInputField(screen, { row: 3, column: 21 })).toBe(true);
    expect(inInputField(screen, { row: 3, column: 25 })).toBe(true);
    expect(inInputField(screen, { row: 3, column: 26 })).toBe(false);
    expect(inInputField(screen, { row: 3, column: 3 })).toBe(false);
    // An output-only (bypass) field takes no typing.
    expect(inInputField(screen, { row: 5, column: 22 })).toBe(false);
  });
});
