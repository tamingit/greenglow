import { describe, expect, it } from 'vitest';
import { writeHodMacro } from '../src/hodmacro.ts';
import { matches } from '../src/player.ts';
import { MacroRecorder, whiteTitle } from '../src/recorder.ts';
import { FFW, ic, sba, sf, show, text } from './screens.ts';

/** White (high-intensity) text, as a title on a screen's first line. */
const white = (s: string) => [0x22, ...text(s), 0x20];

/** A placeholder sign-on: a user field, then a nondisplay password field (attribute 0x27). */
const signOn = (cursorRow: number) =>
  show(
    [
      sba(1, 33),
      white('Sign On'),
      sba(6, 52),
      sf(FFW.alpha, 0x24, 10),
      sba(7, 52),
      sf(FFW.alpha, 0x27, 10),
      ic(cursorRow, 53),
    ].flat(),
  ).screen;

const menu = show(
  [sba(1, 32), white('Main Menu'), sba(20, 6), sf(FFW.alpha, 0x24, 10), ic(20, 7)].flat(),
).screen;

/** A typical application main menu: an ID in normal colour on line 1, no white title. */
const untitled = show(
  [
    sba(1, 2),
    text('MAIN'),
    sba(3, 5),
    white('Select one'),
    sba(20, 6),
    sf(FFW.alpha, 0x24, 10),
    ic(20, 7),
  ].flat(),
).screen;

describe('recording a macro', () => {
  it('records each screen’s keys, and a prompt, never the text, for a password', () => {
    const recorder = new MacroRecorder('Sign on');
    const user = signOn(6);
    const password = signOn(7);
    for (const char of 'PAT') recorder.key({ type: 'character', char }, user);
    recorder.key({ type: 'function', name: 'Tab' }, user);
    for (const char of 'secret1') recorder.key({ type: 'character', char }, password);
    recorder.key({ type: 'function', name: 'Enter' }, password);
    expect(recorder.screens).toBe(1);
    recorder.key({ type: 'character', char: '4' }, menu);
    recorder.key({ type: 'function', name: 'F3' }, menu);

    const macro = recorder.finish(user);
    expect(macro.screens.map((s) => [s.name, s.entry, s.exit, s.next])).toEqual([
      ['Screen1', true, false, ['Screen2']],
      ['Screen2', false, false, ['Screen3']],
      ['Screen3', false, true, []],
    ]);
    const [first, second] = macro.screens;
    expect(first.actions).toEqual([
      expect.objectContaining({ kind: 'input', value: "'PAT[tab]'" }),
      expect.objectContaining({ kind: 'prompt', row: 7, col: 53, len: 10, encrypted: true }),
      expect.objectContaining({ kind: 'input', value: "'[enter]'" }),
    ]);
    expect(second.actions).toEqual([expect.objectContaining({ kind: 'input', value: "'4[pf3]'" })]);
    // Known by its white title on the first line; its field counts are optional, as in ACS.
    expect(first.description).toContainEqual(
      expect.objectContaining({
        kind: 'string',
        value: "'Sign On'",
        row: 1,
        erow: 1,
        optional: false,
      }),
    );
    expect(first.description).toContainEqual(
      expect.objectContaining({ kind: 'numinputfields', number: 2, optional: true }),
    );

    // The password is nowhere in what is saved.
    expect(writeHodMacro(macro)).not.toContain('secret1');
  });

  it('starts an input where a click put the cursor', () => {
    const recorder = new MacroRecorder('Click');
    recorder.key({ type: 'cursor', row: 20, column: 7 }, menu);
    recorder.key({ type: 'character', char: '1' }, menu);
    recorder.key({ type: 'function', name: 'Enter' }, menu);
    const [screen] = recorder.finish(undefined).screens;
    expect(screen.actions).toEqual([
      expect.objectContaining({ kind: 'input', value: "'1[enter]'", row: 20, col: 7 }),
    ]);
  });

  it('takes the title from the white text on the first line only', () => {
    const titled = show(
      [
        sba(1, 2),
        text('PGM100'),
        sba(1, 30),
        white('Work with Placeholders'),
        sba(1, 70),
        white('10/12/26'),
        sba(3, 2),
        white('Long white text below the title'),
      ].flat(),
    ).screen;
    expect(whiteTitle(titled)).toBe('Work with Placeholders');
    expect(whiteTitle(untitled)).toBe('');
  });

  it('starts a macro anywhere when its first screen has no white title, as ACS does', () => {
    const recorder = new MacroRecorder('From the menu');
    recorder.key({ type: 'character', char: '1' }, untitled);
    recorder.key({ type: 'function', name: 'Enter' }, untitled);
    recorder.key({ type: 'function', name: 'F3' }, untitled);
    const [first, second] = recorder.finish(undefined).screens;
    expect(first.description).toEqual([
      { kind: 'oia', status: 'NOTINHIBITED', optional: false, invert: false },
    ]);
    // Later screens without a title keep their counts, optional.
    expect(second.description.map((d) => [d.kind, d.optional])).toEqual([
      ['oia', false],
      ['numfields', true],
      ['numinputfields', true],
    ]);
    // So the first screen matches whatever shows, once the keyboard is free.
    expect(matches(first, menu)).toBe(true);
    expect(matches(first, untitled)).toBe(true);
  });
});
