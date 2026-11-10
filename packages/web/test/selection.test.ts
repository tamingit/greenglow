import { describe, expect, it } from 'vitest';
import { Selection, normalize, selectionText } from '../src/selection.ts';
import { FFW, sba, sf, show, soh, text } from './screens.ts';

const SCREEN = [
  sba(2, 3),
  text('Alpha Beta'),
  sba(3, 3),
  text('Gamma Delta'),
  sba(4, 3),
  text('Epsilon'),
];

describe('the selected rectangle', () => {
  it('is the same whichever way the drag went', () => {
    const rect = { row: 2, column: 5, rows: 3, columns: 4 };
    const a = { row: 2, column: 5 };
    const b = { row: 4, column: 8 };
    expect(normalize(a, b)).toEqual(rect);
    expect(normalize(b, a)).toEqual(rect);
    expect(normalize({ row: 2, column: 8 }, { row: 4, column: 5 })).toEqual(rect);
    expect(normalize({ row: 4, column: 5 }, { row: 2, column: 8 })).toEqual(rect);
  });

  it('is one cell when the corners are the same', () => {
    const cell = { row: 7, column: 9 };
    expect(normalize(cell, cell)).toEqual({ row: 7, column: 9, rows: 1, columns: 1 });
  });
});

describe('the selected text', () => {
  it('is the cells of each row, joined by newlines, with trailing spaces removed', () => {
    const snapshot = show(SCREEN).screen;
    expect(selectionText(snapshot, { row: 2, column: 3, rows: 3, columns: 8 })).toBe(
      'Alpha Be\nGamma De\nEpsilon',
    );
    expect(selectionText(snapshot, { row: 2, column: 9, rows: 2, columns: 4 })).toBe('Beta\nDelt');
    // Left of the text, and a row with nothing in the rectangle.
    expect(selectionText(snapshot, { row: 1, column: 1, rows: 3, columns: 4 })).toBe(
      '\n  Al\n  Ga',
    );
  });

  it('shows attribute bytes and nulls as spaces', () => {
    const snapshot = show([sba(5, 3), text('Name'), sf(FFW.alpha, 0x24, 5), text('Ab')]).screen;
    expect(selectionText(snapshot, { row: 5, column: 3, rows: 1, columns: 12 })).toBe('Name Ab');
  });

  it('stays inside the screen', () => {
    const snapshot = show(SCREEN).screen;
    // Rows below the screen are not there; columns past the edge are cut off.
    expect(selectionText(snapshot, { row: 30, column: 1, rows: 3, columns: 5 })).toBe('');
    expect(selectionText(snapshot, { row: 23, column: 70, rows: 99, columns: 99 })).toBe('\n');
    expect(selectionText(snapshot, { row: 2, column: 3, rows: 1, columns: 500 })).toBe(
      'Alpha Beta',
    );
  });

  it('includes the separate message line as the row below the screen', () => {
    const screen = show([soh(25), ...SCREEN]);
    const snapshot = screen.hostError('Value is not valid.').screen;
    expect(selectionText(snapshot, { row: 24, column: 1, rows: 2, columns: 40 })).toBe(
      '\n Value is not valid.',
    );
  });

  it('copies the operator error text the renderer shows on the error line', () => {
    const screen = show([sba(3, 20), sf(FFW.numeric, 0x24, 5)]);
    screen.operatorError(9); // a letter in a numeric field
    const copied = selectionText(screen.screen, { row: 24, column: 1, rows: 1, columns: 80 });
    expect(copied).toBe(' Field requires numeric characters.');
  });

  it('copies nondisplay input as spaces, never what was typed', () => {
    const screen = show([sba(6, 17), text('Password'), sba(6, 30), sf(FFW.monocase, 0x27, 10)]);
    screen.type('HUNTER2');
    const snapshot = screen.screen;
    expect(snapshot.buffer.some((b) => b !== 0)).toBe(true);
    const copied = selectionText(snapshot, { row: 6, column: 1, rows: 1, columns: 80 });
    expect(copied).toBe('                Password');
    expect(copied).not.toContain('HUNTER2');
  });
});

describe('the selection', () => {
  function make() {
    const shown: (ReturnType<typeof normalize> | undefined)[] = [];
    return { selection: new Selection({ show: (rect) => shown.push(rect) }), shown };
  }

  it('shows a drag, and extends it from the fixed start', () => {
    const { selection, shown } = make();
    selection.drag({ row: 3, column: 5 }, { row: 4, column: 9 });
    expect(selection.extend({ row: 2, column: 2 })).toBe(true);
    expect(selection.rect).toEqual({ row: 2, column: 2, rows: 2, columns: 4 });
    expect(shown.at(-1)).toEqual(selection.rect);
  });

  it('has nothing to extend or copy until a drag starts, and clears', () => {
    const { selection, shown } = make();
    const snapshot = show(SCREEN).screen;
    expect(selection.extend({ row: 1, column: 1 })).toBe(false);
    expect(selection.text(snapshot)).toBeUndefined();
    selection.drag({ row: 2, column: 3 }, { row: 2, column: 7 });
    expect(selection.text(snapshot)).toBe('Alpha');
    selection.clear();
    expect(selection.rect).toBeUndefined();
    expect(shown.at(-1)).toBeUndefined();
  });

  it('survives a new screen of the same size, and is cleared by a different size', () => {
    const { selection } = make();
    selection.see(show(SCREEN).screen);
    selection.drag({ row: 2, column: 3 }, { row: 3, column: 7 });
    selection.see(show([sba(1, 1), text('New')]).screen);
    expect(selection.rect).toBeDefined();
    selection.see(show(SCREEN, { wide: true }).screen);
    expect(selection.rect).toBeUndefined();
  });
});
