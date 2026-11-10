import { describe, expect, it } from 'vitest';
import { DEFAULT_LOOK, LOOKS, loadLook, saveLook } from '../src/looks/look.ts';

function storage(saved?: string) {
  const items = new Map<string, string>(saved ? [['greenglow.look', saved]] : []);
  return {
    items,
    getItem: (key: string) => items.get(key) ?? null,
    setItem: (key: string, value: string) => void items.set(key, value),
  };
}

const blocked = {
  getItem: (): string | null => {
    throw new Error('blocked');
  },
  setItem: (): void => {
    throw new Error('blocked');
  },
};

describe('looks', () => {
  it('always offers Classic', () => {
    expect(LOOKS.map((look) => look.id)).toContain('classic');
  });

  it('uses the saved look, then the default', () => {
    expect(loadLook(storage('night-shift'), '').id).toBe('night-shift');
    expect(loadLook(storage(), '').id).toBe(DEFAULT_LOOK);
    expect(loadLook(undefined, '').id).toBe(DEFAULT_LOOK);
  });

  it('lets ?look= override the saved look, and ignores unknown names', () => {
    expect(loadLook(storage('classic'), '?look=greenglow').id).toBe('greenglow');
    expect(loadLook(storage('classic'), '?share=x&look=bogus').id).toBe('classic');
    expect(loadLook(storage('bogus'), '').id).toBe(DEFAULT_LOOK);
  });

  it('saves the choice, and copes with blocked storage', () => {
    const saved = storage();
    saveLook(saved, LOOKS[1]);
    expect(saved.items.get('greenglow.look')).toBe(LOOKS[1].id);
    expect(() => saveLook(blocked, LOOKS[0])).not.toThrow();
    expect(loadLook(blocked, '').id).toBe(DEFAULT_LOOK);
  });
});
