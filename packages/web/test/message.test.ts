import { describe, expect, it } from 'vitest';
import { recognizeMessage } from '../src/recognize/message.ts';
import { screenView } from '../src/recognize/view.ts';
import { FFW, sba, sf, show, soh, text } from './screens.ts';

const PROMPT = [sba(3, 2), text('Account'), sba(3, 20), sf(FFW.alpha, 0x24, 10)];
const message = (snapshot: Parameters<typeof screenView>[0]) =>
  recognizeMessage(screenView(snapshot), new Set());

describe('message line', () => {
  it('finds a host error written with Write Error Code', () => {
    const screen = show(PROMPT);
    const snapshot = screen.hostError('Value is not valid.').screen;
    expect(snapshot.error?.kind).toBe('host');
    expect(message(snapshot)).toEqual({
      row: 24,
      column: 2,
      length: 'Value is not valid.'.length,
      kind: 'error',
      more: false,
      band: { row: 24, column: 1, length: 80 },
    });
  });

  it('covers an operator error, whose text the renderer draws', () => {
    const screen = show([sba(3, 20), sf(FFW.numeric, 0x24, 5)]);
    const snapshot = screen.operatorError(9).screen; // a letter in a numeric field
    expect(snapshot.error).toMatchObject({ kind: 'operator', code: 9 });
    expect(message(snapshot)).toMatchObject({ row: 24, column: 2, kind: 'error' });
  });

  it("takes white text at the left of the error row as a program's message", () => {
    const snapshot = show([...PROMPT, sba(24, 1), 0x22, text('Order saved. +')]).screen;
    expect(message(snapshot)).toMatchObject({ column: 2, kind: 'info', more: true });
  });

  it('marks it an error when the host reverses an input field', () => {
    const snapshot = show([
      ...PROMPT,
      sba(5, 20),
      sf(FFW.alpha, 0x25, 10), // reverse image: a field in error
      sba(24, 1),
      0x22,
      text('Customer not found.'),
    ]).screen;
    expect(message(snapshot)?.kind).toBe('error');
  });

  it('ignores ordinary text, legends and the copyright line on the error row', () => {
    const green = show([...PROMPT, sba(24, 1), 0x20, text('Some label')]).screen;
    expect(message(green)).toBeUndefined();
    const legend = show([...PROMPT, sba(24, 1), 0x22, text('F3=Exit   F12=Cancel')]).screen;
    expect(message(legend)).toBeUndefined();
    const copyright = show([...PROMPT, sba(24, 40), 0x22, text('(C) COPYRIGHT IBM CORP.')]).screen;
    expect(message(copyright)).toBeUndefined();
    expect(message(show(PROMPT).screen)).toBeUndefined();
  });

  it('reads the separate message line below the screen', () => {
    const screen = show([soh(25), ...PROMPT]);
    const snapshot = screen.hostError('Value is not valid.').screen;
    expect(snapshot.errorRow).toBe(25);
    expect(message(snapshot)).toMatchObject({ row: 25, column: 2, kind: 'error' });
  });
});
