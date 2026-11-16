import { describe, expect, it } from 'vitest';
import { pathProblem, remember, sizeText } from '../src/files.ts';

describe('Files panel', () => {
  it('says sizes as people do', () => {
    expect(sizeText(0)).toBe('0 bytes');
    expect(sizeText(1)).toBe('1 byte');
    expect(sizeText(1023)).toBe('1023 bytes');
    expect(sizeText(1536)).toBe('1.5 KB');
    expect(sizeText(29261)).toBe('29 KB');
    expect(sizeText(5 * 1024 * 1024)).toBe('5.0 MB');
    expect(sizeText(3 * 1024 ** 4)).toBe('3.0 TB');
    expect(sizeText(-1)).toBe('');
  });

  it('asks only for absolute paths', () => {
    expect(pathProblem('')).toMatch(/Type the path/);
    expect(pathProblem('home/me/x')).toMatch(/starts with \//);
    expect(pathProblem('/home/me/x')).toBeUndefined();
  });

  it('keeps recent paths newest first, each once', () => {
    let recent: string[] = [];
    for (const path of ['/a', '/b', '/a', '/c', '/d', '/e', '/f']) recent = remember(recent, path);
    expect(recent).toEqual(['/f', '/e', '/d', '/c', '/a']);
  });
});
