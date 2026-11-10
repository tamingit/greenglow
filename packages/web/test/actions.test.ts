import { describe, expect, it } from 'vitest';
import { keysFor, runKeys } from '../src/actions.ts';
import { decorate, hotspots } from '../src/decorate/index.ts';
import { targetAt } from '../src/pointer.ts';
import type { Key, OutboundKind } from '../src/protocol/index.ts';
import { FFW, sba, sf, show, soh, text, type TestScreen } from './screens.ts';

const LEGEND_SCREEN = [
  sba(3, 2),
  text('Amount'),
  sba(3, 20),
  sf(FFW.numeric, 0x24, 5),
  sba(23, 1),
  0x3a,
  text('F3=Exit   F12=Cancel'),
];

/**
 * Runs keys against a screen, collecting what they send. The keyboard follows the 5250
 * rules the clicks rely on (the engine's own tests cover them in full): any key but Reset
 * clears an error and then acts, Reset only clears it (a keyboard the host locked stays
 * locked), a key is typed ahead while the keyboard is locked, and an AID key on an
 * unlocked keyboard sends the READ reply and locks it.
 */
function context(connection: TestScreen) {
  const sent: { kind: OutboundKind }[] = [];
  const press = (key: Key): void => {
    if (connection.error) {
      connection.reset();
      if (key.type === 'function' && key.name === 'Reset') return;
    }
    if (connection.locked) {
      connection.typeAhead++;
      return;
    }
    if (key.type === 'function' && key.name !== 'Reset') {
      sent.push({ kind: 'input' });
      connection.lock();
      connection.pendingRead = undefined;
    }
  };
  return { sent, live: () => connection.screen, press };
}

describe('clicks', () => {
  it('in Classic, the function keys the screen names are hotspots, the same as the pills', () => {
    const connection = show(LEGEND_SCREEN);
    const spots = hotspots(connection.screen);
    expect(spots).toEqual(decorate(connection.screen).targets);
    expect(targetAt(spots, { row: 23, column: 2 })?.label).toBe('Exit (F3)');
  });

  it('finds the target under a cell, and presses the key the screen names', () => {
    const connection = show(LEGEND_SCREEN);
    const decorations = decorate(connection.screen);
    const target = targetAt(decorations.targets, { row: 23, column: 14 });
    expect(target?.label).toBe('Cancel (F12)');
    expect(targetAt(decorations.targets, { row: 23, column: 10 })).toBeUndefined();
    const keys = target && keysFor(target.action, connection.screen);
    expect(keys).toEqual([{ type: 'function', name: 'F12' }]);
    const ctx = context(connection);
    expect(runKeys(keys ?? [], ctx)).toBe('done');
    expect(ctx.sent.map((s) => s.kind)).toEqual(['input']);
    expect(connection.screen.keyboard).toBe('locked');
  });

  it('does nothing when the screen no longer shows what was clicked', () => {
    const connection = show(LEGEND_SCREEN);
    const [, pill] = decorate(connection.screen).targets;
    connection.write([sba(23, 2), text('F3=Back ')]);
    expect(keysFor(pill.action, connection.screen)).toEqual([{ type: 'function', name: 'F12' }]);
    const [exit] = decorate(show(LEGEND_SCREEN).screen).targets;
    expect(keysFor(exit.action, connection.screen)).toBeUndefined();
  });

  it('is never typed ahead: refused while the host has the keyboard', () => {
    const connection = show(LEGEND_SCREEN);
    const ctx = context(connection);
    runKeys([{ type: 'function', name: 'Enter' }], ctx);
    expect(connection.screen.keyboard).toBe('locked');
    expect(runKeys([{ type: 'function', name: 'F3' }], ctx)).toBe('refused');
    expect(connection.screen.typeAhead).toBe(0);
    expect(ctx.sent).toHaveLength(1);
  });

  it('clears an error with Reset first, as any key does in ACS', () => {
    const connection = show(LEGEND_SCREEN);
    connection.operatorError(9); // a letter in a numeric field
    expect(connection.screen.keyboard).toBe('error');
    const ctx = context(connection);
    expect(runKeys([{ type: 'function', name: 'F3' }], ctx)).toBe('done');
    expect(ctx.sent.map((s) => s.kind)).toEqual(['input']);
    expect(connection.screen.error).toBeUndefined();
  });

  it('stops after Reset when the host locked the keyboard during the error', () => {
    const connection = show(LEGEND_SCREEN);
    const ctx = context(connection);
    connection.hostError('Value is not valid.');
    // A later write with Start of Header locks the keyboard under the error.
    connection.write([soh(24)]);
    expect(connection.screen.keyboard).toBe('error');
    expect(runKeys([{ type: 'function', name: 'F3' }], ctx)).toBe('stopped');
    expect(connection.screen.keyboard).toBe('locked');
    expect(connection.screen.typeAhead).toBe(0);
    expect(ctx.sent).toEqual([]);
  });
});
