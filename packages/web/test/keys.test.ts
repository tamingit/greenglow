import { describe, expect, it } from 'vitest';
import { KEY_CODES, tokenize } from '../src/recognize/codes.ts';
import { decorate } from '../src/decorate/index.ts';
import { recognize } from '../src/recognize/index.ts';
import { recognizeKeys } from '../src/recognize/keys.ts';
import { screenView } from '../src/recognize/view.ts';
import { FFW, sba, sf, show, text } from './screens.ts';

const items = (line: string, dash = true) =>
  tokenize(line, KEY_CODES, { dash }).items.map(({ code, label, column, end }) => [
    code,
    label,
    column,
    end,
  ]);

describe('code legends', () => {
  it('splits items at two spaces, keeping single spaces and slashes in labels', () => {
    expect(items('F3=Exit   F12=Cancel')).toEqual([
      ['F3', 'Exit', 1, 8],
      ['F12', 'Cancel', 11, 21],
    ]);
    expect(items('F14=Aaaa Bbbb/Cccc Dddd   F23=Eeee').map((i) => i[1])).toEqual([
      'Aaaa Bbbb/Cccc Dddd',
      'Eeee',
    ]);
  });

  it('starts a new item after one space, and allows spaces around "="', () => {
    expect(items('F18=Aaaa Bbbb F19= CCCC DDDD').map((i) => i[1])).toEqual([
      'Aaaa Bbbb',
      'CCCC DDDD',
    ]);
    expect(items('ENTER=Process, F12 = Return').map((i) => [i[0], i[1]])).toEqual([
      ['ENTER', 'Process'],
      ['F12', 'Return'],
    ]);
  });

  it('takes "F12-Return" only when dashes are allowed', () => {
    expect(items('F12-Return   F3-Cancel').map((i) => i[1])).toEqual(['Return', 'Cancel']);
    expect(items('F12-Return', false)).toEqual([]);
  });

  it('leaves what it cannot explain in the rest', () => {
    expect(tokenize('F25=Bad', KEY_CODES).items).toEqual([]);
    expect(tokenize('Press Enter  F3=Exit', KEY_CODES).rest.trim()).toBe('Press Enter');
  });
});

const legend = (orders: (number | number[])[], wide = false) =>
  recognizeKeys(screenView(show(orders, { wide }).screen), new Set());

describe('function-key legends', () => {
  it('finds a legend near the bottom, in any colour, and names the keys', () => {
    const [found] = legend([sba(23, 1), 0x3a, text('F3=Exit   F12=Cancel')]);
    expect(found.row).toBe(23);
    expect(found.items.map((i) => [i.key, i.label, i.span.column, i.span.length])).toEqual([
      ['F3', 'Exit', 2, 7],
      ['F12', 'Cancel', 12, 10],
    ]);
    expect(legend([sba(23, 1), 0x20, text('F3=Exit')])).toHaveLength(1);
  });

  it('maps the named keys to their AIDs', () => {
    const [found] = legend([sba(24, 1), 0x20, text('Enter=Go  PageDown=More  PageUp=Back')]);
    expect(found.items.map((i) => i.key)).toEqual(['Enter', 'RollUp', 'RollDown']);
  });

  it('finds the legend on a 27 x 132 screen', () => {
    expect(legend([sba(26, 5), text('F4=Prompt   F12=Return')], true)[0].row).toBe(26);
  });

  it('ignores rows with other text, legends higher up, F25, and text inside fields', () => {
    expect(legend([sba(23, 2), text('Press Enter  F3=Exit')])).toEqual([]);
    expect(legend([sba(12, 2), text('F3=Exit   F12=Cancel')])).toEqual([]);
    expect(legend([sba(23, 2), text('F25=Bad')])).toEqual([]);
    expect(legend([sba(23, 2), sf(FFW.bypass, 0x20, 30), text('F3=Exit   F12=Cancel')])).toEqual(
      [],
    );
  });

  it('claims its row, so a legend on the error row is not a message', () => {
    const view = screenView(
      show([sba(3, 2), sf(FFW.alpha, 0x24, 5), sba(24, 1), 0x22, text('F3=Exit')]).screen,
    );
    const recognized = recognize(view);
    expect(recognized.scopes[0].keys[0].row).toBe(24);
    expect(recognized.message).toBeUndefined();
  });
});

describe('bare roll keys in a legend', () => {
  const line = 'F3=Exit  F6=Add Record  F16=Reposition  Rolldown';
  const bare = (words: string) => legend([sba(23, 1), 0x3a, text(words)])[0];

  it('makes a bare roll key an item that names its own key', () => {
    const found = bare(line);
    expect(found.items.map((i) => [i.code, i.key, i.label, i.span.column, i.span.length])).toEqual([
      ['F3', 'F3', 'Exit', 2, 7],
      ['F6', 'F6', 'Add Record', 11, 13],
      ['F16', 'F16', 'Reposition', 26, 14],
      ['Rolldown', 'RollDown', '', 42, 8],
    ]);
  });

  it('knows the spellings of both roll keys', () => {
    expect(bare('F3=Exit  Rollup').items[1].key).toBe('RollUp');
    expect(bare('F3=Exit  PageDown  PageUp').items.map((i) => i.key)).toEqual([
      'F3',
      'RollUp',
      'RollDown',
    ]);
  });

  it('still rejects any other leftover word, and a roll key with nothing else', () => {
    expect(bare('F3=Exit  Rolldown  Quit')).toBeUndefined();
    expect(bare('Rolldown')).toBeUndefined();
    // A word that only contains a roll key's name is not one.
    expect(bare('F3=Exit  Rolldowns')).toBeUndefined();
  });

  it('draws a bare roll key as a pill that presses it', () => {
    const decorations = decorate(show([sba(23, 1), 0x3a, text(line)]).screen);
    const target = decorations.targets.find((t) => t.id === 'pill:23:42');
    expect(target?.action).toMatchObject({ type: 'key', key: 'RollDown' });
    expect(target?.label).toBe('Page Up');
    // No keycap behind it: the whole word is the pill.
    expect(decorations.boxes.some((b) => b.id === 'keycap:23:42')).toBe(false);
    expect(decorations.text.find((t) => t.span.column === 42)?.className).toBe('g-pill');
  });
});
