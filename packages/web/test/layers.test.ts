import { describe, expect, it } from 'vitest';
import type { Box } from '../src/decorations.ts';
import { planLayer, signature } from '../src/layers.ts';

const box = (id: string, column: number, modifiers: string[] = []): Box => ({
  id,
  kind: 'field',
  rect: { row: 3, column, rows: 1, columns: 5 },
  modifiers,
});

describe('layer changes', () => {
  it('adds new boxes, updates changed ones, removes gone ones and leaves the rest', () => {
    const before = [box('a', 2), box('b', 10, ['focus']), box('c', 20)];
    const painted = new Map(before.map((b) => [b.id, signature(b)]));
    const plan = planLayer(painted, [box('a', 2), box('b', 10), box('d', 30)]);
    expect(plan.add.map((b) => b.id)).toEqual(['d']);
    expect(plan.update.map((b) => b.id)).toEqual(['b']);
    expect(plan.remove).toEqual(['c']);
  });
});
