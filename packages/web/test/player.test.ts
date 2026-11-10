import { describe, expect, it } from 'vitest';
import type { HodMacro } from '../src/hodmacro.ts';
import { keysOf, matches, playMacro, type PlayerHost } from '../src/player.ts';
import type { Key, ScreenSnapshot } from '../src/protocol/index.ts';
import { MacroRecorder } from '../src/recorder.ts';
import { FFW, ic, sba, sf, show, text } from './screens.ts';

/** White (high-intensity) text: a title on a screen's first line, which a recording keys on. */
const white = (s: string) => [0x22, ...text(s), 0x20];

const signOn = show(
  [
    sba(1, 33),
    white('Sign On'),
    sba(6, 52),
    sf(FFW.alpha, 0x24, 10),
    sba(7, 52),
    sf(FFW.alpha, 0x27, 10),
    ic(6, 53),
  ].flat(),
).screen;
const menu = show(
  [sba(1, 32), white('Main Menu'), sba(20, 6), sf(FFW.alpha, 0x24, 10), ic(20, 7)].flat(),
).screen;

/** A sign-on macro, recorded: user, a prompt for the password, Enter; then the menu. */
function recorded(): HodMacro {
  const recorder = new MacroRecorder('Sign on');
  for (const char of 'PAT') recorder.key({ type: 'character', char }, signOn);
  recorder.key({ type: 'function', name: 'Tab' }, signOn);
  const atPassword = { ...signOn, cursor: { row: 7, column: 53 } };
  recorder.key({ type: 'character', char: 'x' }, atPassword);
  recorder.key({ type: 'function', name: 'Enter' }, atPassword);
  return recorder.finish(menu);
}

/** A host whose screen moves to the menu at Enter. */
function host(start: ScreenSnapshot, answers: string[] = []): PlayerHost & { pressed: Key[] } {
  let now = start;
  const pressed: Key[] = [];
  return {
    pressed,
    screen: () => now,
    press(key) {
      pressed.push(key);
      if (key.type === 'function' && key.name === 'Enter') now = menu;
    },
    waitFor: (match) => Promise.resolve(match(now) ? now : undefined),
    answer: () => Promise.resolve(now),
    ask: () => Promise.resolve(answers.shift()),
    tell: () => Promise.resolve(),
    sleep: () => Promise.resolve(),
    cancelled: () => false,
  };
}

describe('playing a macro', () => {
  it('turns HOD text into keys', () => {
    expect(keysOf("'4[enter]'")).toEqual([
      { type: 'character', char: '4' },
      { type: 'function', name: 'Enter' },
    ]);
    expect(keysOf("'[PF3][pagedn]'")).toEqual([
      { type: 'function', name: 'F3' },
      { type: 'function', name: 'RollUp' },
    ]);
    // Not a host key: typed as it is.
    expect(
      keysOf("'[x]'")
        .map((k) => (k.type === 'character' ? k.char : '?'))
        .join(''),
    ).toBe('[x]');
  });

  it('recognises a screen by its descriptions', () => {
    const [first, last] = [recorded().screens[0], recorded().screens.at(-1)!];
    expect(matches(first, signOn)).toBe(true);
    expect(matches(first, menu)).toBe(false);
    expect(matches(last, menu)).toBe(true);
  });

  it('plays a recorded sign-on, asking for the password', async () => {
    const played = host(signOn, ['pw']);
    const result = await playMacro(recorded(), played);
    expect(result).toMatchObject({ done: true, screens: 2 });
    const typed = played.pressed.map((k) =>
      k.type === 'character'
        ? k.char
        : k.type === 'function'
          ? `[${k.name}]`
          : `@${k.row},${k.column}`,
    );
    expect(typed.join('')).toBe('PAT[Tab]@7,53[EraseEof]pw[Enter]');
  });

  it('stops when the screen isn’t the one the macro starts on, or a prompt is cancelled', async () => {
    expect((await playMacro(recorded(), host(menu))).stopped).toBe(
      'the screen showing isn’t the one the macro starts on',
    );
    expect((await playMacro(recorded(), host(signOn, []))).stopped).toBe('you cancelled a prompt');
  });
});
