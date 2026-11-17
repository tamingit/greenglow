import { describe, expect, it } from 'vitest';
import {
  entryDetail,
  joinPath,
  listingText,
  parentOf,
  pathProblem,
  remember,
  sizeText,
  uploadItems,
  uploadSummary,
  type UploadItem,
} from '../src/files.ts';

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
    expect(pathProblem('')).toMatch(/Type a path/);
    expect(pathProblem('home/me/x')).toMatch(/starts with \//);
    expect(pathProblem('/home/me/x')).toBeUndefined();
  });

  it('goes up and into folders', () => {
    expect(parentOf('/home/me/docs')).toBe('/home/me');
    expect(parentOf('/home/me/')).toBe('/home');
    expect(parentOf('/home')).toBe('/');
    expect(parentOf('/')).toBe('/');
    expect(joinPath('/', 'home')).toBe('/home');
    expect(joinPath('/home/me', 'a.txt')).toBe('/home/me/a.txt');
  });

  it('says a file by its size and date, a folder by its date', () => {
    const modified = '2026-11-16T09:30:00+00:00';
    expect(entryDetail({ name: 'a.txt', folder: false, size: 2048, modified })).toMatch(
      /^2\.0 KB · /,
    );
    expect(entryDetail({ name: 'docs', folder: true, size: null, modified })).not.toMatch(
      /KB|bytes/,
    );
  });

  it('counts a listing, and says when it was cut short', () => {
    const entry = (name: string, folder: boolean) => ({
      name,
      folder,
      size: folder ? null : 1,
      modified: '',
    });
    expect(listingText({ path: '/', truncated: false, entries: [] })).toBe('This folder is empty.');
    expect(
      listingText({
        path: '/',
        truncated: false,
        entries: [entry('a', true), entry('b', false), entry('c', false)],
      }),
    ).toBe('1 folder, 2 files.');
    expect(
      listingText({ path: '/', truncated: true, entries: [entry('a', true), entry('b', true)] }),
    ).toBe('The first 2 entries: 2 folders.');
  });

  it('keeps recent paths newest first, each once', () => {
    let recent: string[] = [];
    for (const path of ['/a', '/b', '/a', '/c', '/d', '/e', '/f']) recent = remember(recent, path);
    expect(recent).toEqual(['/f', '/e', '/d', '/c', '/a']);
  });

  it('puts uploads at their relative paths in the folder', () => {
    const file = new File(['x'], 'a.txt');
    const items = uploadItems('/home/me', [
      { file, relative: 'a.txt' },
      { file, relative: 'docs/sub/b.txt' },
      { file: null, relative: 'docs/empty' },
    ]);
    expect(items.map((i) => i.path)).toEqual([
      '/home/me/a.txt',
      '/home/me/docs/sub/b.txt',
      '/home/me/docs/empty',
    ]);
    expect(items.every((i) => i.state === 'waiting')).toBe(true);
    expect(uploadItems('/', [{ file, relative: '/x.txt' }])[0]?.path).toBe('/x.txt');
  });

  it('sums up an upload', () => {
    const item = (state: UploadItem['state']): UploadItem => ({
      file: null,
      path: '/a',
      state,
      note: '',
    });
    expect(uploadSummary([item('done'), item('done')])).toBe('Uploaded 2 files.');
    expect(uploadSummary([item('done'), item('exists'), item('failed')])).toBe(
      'Uploaded 1 file. 1 file already there, not replaced. 1 file failed.',
    );
    expect(uploadSummary([item('done'), item('stopped'), item('stopped')])).toBe(
      'Uploaded 1 file. Stopped.',
    );
    expect(uploadSummary([])).toBe('Nothing to upload.');
  });
});
