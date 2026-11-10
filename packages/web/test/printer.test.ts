import { describe, expect, it } from 'vitest';
import {
  cannotOpenText,
  createdText,
  fileDetail,
  nextRow,
  printerView,
  type SpooledFileInfo,
} from '../src/printer.ts';

const file: SpooledFileInfo = {
  id: 'a'.repeat(24),
  name: 'QSYSPRT',
  number: 3,
  job: '123456/PRTUSER/QPADEV0001',
  jobSystem: 'FAKEIBMI',
  created: '2026-10-12T09:30:00',
  status: '*READY',
  pages: 2,
  userData: 'PLACEHOLDR',
  type: '*SCS',
  outputQueue: 'QUSRSYS/QPRINT',
  formType: '*STD',
  canOpen: true,
};

describe('printer output panel', () => {
  it('moves through the list with the arrow, Home, End and page keys', () => {
    expect(nextRow(0, 5, 'ArrowDown')).toBe(1);
    expect(nextRow(4, 5, 'ArrowDown')).toBe(4);
    expect(nextRow(0, 5, 'ArrowUp')).toBe(0);
    expect(nextRow(3, 5, 'Home')).toBe(0);
    expect(nextRow(0, 5, 'End')).toBe(4);
    expect(nextRow(0, 30, 'PageDown')).toBe(10);
    expect(nextRow(5, 30, 'PageUp')).toBe(0);
    // Other keys, and an empty list, are left alone.
    expect(nextRow(0, 5, 'Enter')).toBeUndefined();
    expect(nextRow(0, 0, 'ArrowDown')).toBeUndefined();
    // From outside the list, Down goes to the first row.
    expect(nextRow(-1, 5, 'ArrowDown')).toBe(0);
  });

  it('shows the time for a file made today, else the date', () => {
    const today = createdText('2026-10-12T09:30:00', new Date('2026-10-12T15:00:00'));
    const earlier = createdText('2026-10-01T09:30:00', new Date('2026-10-12T15:00:00'));
    expect(today).toMatch(/9:30/);
    expect(today).not.toMatch(/2026/);
    expect(earlier).toMatch(/2026/);
    expect(createdText(null)).toBe('');
    expect(createdText('not a date')).toBe('');
  });

  it('shows the list only for the same system and IBM i user', () => {
    const prod = { name: 'prod', label: 'Production', ibmUser: 'PAT' };
    // Signed on as the tab's user, or the tab isn't signed on: the list.
    expect(printerView(prod, 'PAT')).toEqual({ list: true });
    expect(printerView({ name: 'prod', label: 'Production' }, 'PAT')).toEqual({ list: true });
    // Signed on as someone else: sign on as the tab's user.
    expect(printerView(prod, 'SAM')).toEqual({ list: false, user: 'PAT', other: 'SAM' });
    // Not signed on to printer output on this system.
    expect(printerView(prod, null)).toEqual({ list: false, user: 'PAT', other: undefined });
  });

  it("says what tells a file apart, and why one can't be opened", () => {
    const [when, where] = fileDetail(file, new Date('2026-10-12T15:00:00'));
    expect(when).toMatch(/9:30.* · 2 pages · \*READY$/);
    expect(where).toBe('job 123456/PRTUSER/QPADEV0001 · file 3 · QUSRSYS/QPRINT');
    expect(fileDetail({ ...file, pages: 1, created: null, outputQueue: '' })).toEqual([
      '1 page · *READY',
      'job 123456/PRTUSER/QPADEV0001 · file 3',
    ]);
    expect(cannotOpenText({ ...file, type: '*AFPDS', canOpen: false })).toBe(
      "*AFPDS can't be shown here: open it in ACS.",
    );
  });
});
