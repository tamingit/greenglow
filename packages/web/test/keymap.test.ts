import { describe, expect, it } from 'vitest';
import { SOLO_MODIFIERS, mapKey, type KeyInput } from '../src/keymap.ts';

const press = (code: string, key: string, modifiers: Partial<KeyInput> = {}) =>
  mapKey({
    code,
    key,
    shiftKey: false,
    ctrlKey: false,
    altKey: false,
    metaKey: false,
    ...modifiers,
  });

const fn = (name: string) => ({ type: 'function', name });

describe('ACS key map', () => {
  it('maps the keys taken from ACS', () => {
    expect(press('Enter', 'Enter')).toEqual(fn('Enter'));
    expect(press('NumpadEnter', 'Enter')).toEqual(fn('Enter'));
    expect(press('Enter', 'Enter', { ctrlKey: true })).toEqual(fn('FieldExit'));
    expect(press('NumpadEnter', 'Enter', { ctrlKey: true })).toEqual(fn('FieldExit'));
    expect(press('Enter', 'Enter', { shiftKey: true })).toEqual(fn('NewLine'));
    expect(press('NumpadAdd', '+')).toEqual(fn('FieldPlus'));
    expect(press('NumpadSubtract', '-')).toEqual(fn('FieldMinus'));
    expect(press('Escape', 'Escape')).toEqual(fn('Attention'));
    expect(press('Escape', 'Escape', { shiftKey: true })).toEqual(fn('SystemRequest'));
    expect(SOLO_MODIFIERS).toEqual({ ControlLeft: 'Reset', ControlRight: 'NewLine' });
  });

  it('maps function keys, Shift for F13 to F24, and the page keys to Roll', () => {
    expect(press('F3', 'F3')).toEqual(fn('F3'));
    expect(press('F12', 'F12', { shiftKey: true })).toEqual(fn('F24'));
    expect(press('PageDown', 'PageDown')).toEqual(fn('RollUp'));
    expect(press('PageUp', 'PageUp')).toEqual(fn('RollDown'));
    expect(press('Tab', 'Tab', { shiftKey: true })).toEqual(fn('Backtab'));
  });

  it('types printable characters, including shifted ones and the keypad digits', () => {
    expect(press('KeyA', 'a')).toEqual({ type: 'character', char: 'a' });
    expect(press('KeyA', 'A', { shiftKey: true })).toEqual({ type: 'character', char: 'A' });
    expect(press('Numpad7', '7')).toEqual({ type: 'character', char: '7' });
    expect(press('Space', ' ')).toEqual({ type: 'character', char: ' ' });
  });

  it('leaves Ctrl, Alt and Meta combinations and lone modifiers to the browser', () => {
    expect(press('KeyC', 'c', { ctrlKey: true })).toBeUndefined();
    expect(press('KeyV', 'v', { metaKey: true })).toBeUndefined();
    expect(press('ControlRight', 'Control', { ctrlKey: true })).toBeUndefined();
    expect(press('ShiftLeft', 'Shift', { shiftKey: true })).toBeUndefined();
  });
});
