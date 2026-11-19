import { describe, expect, it } from 'vitest';
import { gridFor, shownTabs } from '../src/tiles.ts';

describe('gridFor', () => {
  it('takes a fixed layout as it is', () => {
    expect(gridFor('3x2', 2, 1600, 900)).toEqual({ columns: 3, rows: 2 });
    expect(gridFor('1x2', 5, 1600, 900)).toEqual({ columns: 1, rows: 2 });
  });

  it('fits the sessions to a wide window', () => {
    expect(gridFor('auto', 1, 1600, 900)).toEqual({ columns: 1, rows: 1 });
    expect(gridFor('auto', 2, 1600, 900)).toEqual({ columns: 2, rows: 1 });
    expect(gridFor('auto', 4, 1600, 900)).toEqual({ columns: 2, rows: 2 });
    expect(gridFor('auto', 5, 1600, 900)).toEqual({ columns: 3, rows: 2 });
    expect(gridFor('auto', 6, 1600, 900)).toEqual({ columns: 3, rows: 2 });
  });

  it('stacks two sessions in a tall window', () => {
    expect(gridFor('auto', 2, 800, 1200)).toEqual({ columns: 1, rows: 2 });
  });

  it('never goes past 3 by 2', () => {
    expect(gridFor('auto', 10, 4000, 400)).toEqual({ columns: 3, rows: 2 });
  });
});

describe('shownTabs', () => {
  it('shows every tab when they fit, in tab order', () => {
    expect(shownTabs([1, 2, 3], [3, 1, 2], 4)).toEqual([1, 2, 3]);
  });

  it('shows those used most recently, in tab order', () => {
    expect(shownTabs([1, 2, 3, 4, 5], [5, 2, 4, 1, 3], 2)).toEqual([2, 5]);
  });

  it('fills up with tabs never used', () => {
    expect(shownTabs([1, 2, 3, 4], [3], 2)).toEqual([1, 3]);
  });
});
