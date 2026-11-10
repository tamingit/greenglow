import { describe, expect, it } from 'vitest';
import { parseTheme, themeFile } from '../src/themes.ts';

describe('theme files', () => {
  it('round-trips a theme', () => {
    const theme = {
      name: 'Amber',
      base: 'classic' as const,
      colors: { '--term-bg': '#101010', '--term-green': '#ffb000' },
    };
    expect(parseTheme(themeFile(theme))).toEqual(theme);
  });

  it('takes only #rrggbb colours, so a file carries nothing else into the page', () => {
    const file = (colors: object) =>
      JSON.stringify({
        format: 'greenglow-theme',
        version: 1,
        name: 'x',
        base: 'classic',
        colors,
      });
    expect(parseTheme(file({ '--term-bg': 'url(https://example.com/x)' }))).toBe(
      '--term-bg isn’t a #rrggbb colour.',
    );
    expect(parseTheme(file({ '--term-bg': 'red; background: red' }))).toBe(
      '--term-bg isn’t a #rrggbb colour.',
    );
    // Tokens a theme doesn't set are dropped, not applied.
    expect(parseTheme(file({ '--anything': '#000000', '--term-red': '#AA0000' }))).toEqual({
      name: 'x',
      base: 'classic',
      colors: { '--term-red': '#aa0000' },
    });
  });

  it('says what is wrong with a file that isn’t a theme', () => {
    expect(parseTheme('nope')).toBe('This file isn’t a theme: it isn’t JSON.');
    expect(parseTheme('{}')).toBe('This file isn’t a GreenGlow theme.');
    expect(
      parseTheme(JSON.stringify({ format: 'greenglow-theme', version: 2, base: 'classic' })),
    ).toBe('This theme is from a newer GreenGlow.');
    expect(
      parseTheme(JSON.stringify({ format: 'greenglow-theme', version: 1, base: 'neon' })),
    ).toBe('This theme is for a look this page lacks.');
  });
});
