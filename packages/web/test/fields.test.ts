import { describe, expect, it } from 'vitest';
import { decorate } from '../src/decorate/index.ts';
import { fieldRules, fieldSpans } from '../src/decorate/fields.ts';
import { screenView } from '../src/recognize/view.ts';
import { decodeFieldFormat } from '../src/protocol/index.ts';
import { rowRuns, rowStyles } from '../src/text.ts';
import { FFW, sba, sf, show, text } from './screens.ts';

/** A sign-on-like screen: a user field, a nondisplay password and a hidden bypass field. */
const SIGN_ON = [
  sba(6, 17),
  text('User'),
  sba(6, 52),
  sf(FFW.monocase, 0x24, 10),
  sba(7, 17),
  text('Password'),
  sba(7, 52),
  sf(FFW.monocase, 0x27, 10),
  sba(8, 52),
  sf(FFW.bypass, 0x27, 10),
];

const boxIds = (boxes: readonly { id: string }[]) => boxes.map((box) => box.id);

describe('field boxes', () => {
  it('boxes input fields but not bypass fields, and focuses the one with the cursor', () => {
    const decorations = decorate(show(SIGN_ON).screen);
    expect(boxIds(decorations.boxes)).toEqual(['field:6:53', 'field:7:53']);
    expect(decorations.boxes[0].modifiers).toEqual(['focus']);
    expect(decorations.boxes[1].modifiers).toEqual(['secret']);
    expect(decorations.fieldInfo).toBe('Uppercase · 10 positions');
    expect(decorations.masks).toEqual([{ row: 7, column: 53, length: 10 }]);
  });

  it('shows typed nondisplay input as bullets, and never the characters', () => {
    const screen = show(SIGN_ON);
    screen.cursorTo(7, 53).type('SECRET');
    const snapshot = screen.screen;
    const decorations = decorate(snapshot);
    expect(JSON.stringify(decorations)).not.toContain('SECRET');
    expect(screenView(snapshot).line(7)).not.toContain('SECRET');
    const styles = rowStyles(decorations.text, decorations.masks, 24, 80);
    const start = 6 * 80;
    const runs = rowRuns(
      snapshot.buffer.subarray(start, start + 80),
      snapshot.attributes.subarray(start, start + 80),
      styles[6],
    );
    const shown = runs.map((run) => run.text).join('');
    expect(shown).not.toContain('SECRET');
    expect(shown.slice(52, 62)).toBe('••••••    ');
    expect(decorations.boxes[1].modifiers).toEqual(['focus', 'secret']);
  });

  it('splits a field that wraps into one box per row', () => {
    const field = show([sba(1, 74), sf(FFW.alpha, 0x24, 10)]).screen.fields[0];
    expect(fieldSpans(field, 80)).toEqual([
      { row: 1, column: 75, length: 6 },
      { row: 2, column: 1, length: 4 },
    ]);
  });

  it('marks required, numeric, red and reversed fields', () => {
    const decorations = decorate(
      show([
        sba(3, 20),
        sf(FFW.mandatory, 0x24, 5),
        sba(4, 20),
        sf(FFW.numeric, 0x24, 5),
        sba(5, 20),
        sf(FFW.alpha, 0x2c, 5), // red, underlined
        sba(6, 20),
        sf(FFW.alpha, 0x25, 5), // reverse image
      ]).screen,
    );
    expect(decorations.boxes.map((box) => box.modifiers)).toEqual([
      ['focus', 'required'],
      ['numeric'],
      ['red'],
      ['reverse'],
    ]);
  });

  it('outlines the field with the cursor, and reversed fields, while an error shows', () => {
    const screen = show([
      sba(3, 20),
      sf(FFW.alpha, 0x24, 5),
      sba(4, 20),
      sf(FFW.alpha, 0x25, 5),
      sba(5, 20),
      sf(FFW.alpha, 0x24, 5),
    ]);
    const snapshot = screen.hostError('Value is not valid.').screen;
    const decorations = decorate(snapshot);
    expect(decorations.boxes.map((box) => box.id)).toEqual([
      'message',
      'field:3:21',
      'field:4:21',
      'field:5:21',
    ]);
    expect(decorations.boxes[0].modifiers).toEqual(['error', 'icon']);
    expect(decorations.boxes[1].modifiers).toEqual(['focus', 'error']);
    expect(decorations.boxes[2].modifiers).toEqual(['error', 'reverse']);
    expect(decorations.boxes[3].modifiers).toEqual([]);
  });

  it('describes field rules in words', () => {
    const rules = (ffw: number, length: number, secret = false) =>
      fieldRules(decodeFieldFormat(ffw), length, secret);
    expect(rules(FFW.mandatory | 0x0300, 10)).toBe('Numbers only · 10 positions · Required');
    expect(rules(FFW.signed, 7)).toBe('Signed number · 6 digits');
    expect(rules(FFW.rightZero, 9)).toBe('Numbers only · 9 positions · Right-adjusts with zeros');
    expect(rules(FFW.autoEnter, 1)).toBe('1 position · Sends when full');
    expect(rules(FFW.monocase, 10, true)).toBe('Uppercase · 10 positions · Hidden');
  });
});
