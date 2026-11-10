// Runs the GreenGlow decorations over every screen of the real recordings in
// test/recordings/ (gitignored) when there are any. They exist only on a developer's
// machine, so this checks behaviour without storing their contents: no snapshots, no
// printed screens. The C# engine replays them (`snapshots` in server/tools/GreenGlow.Tools),
// handing each screen over as the JSON the server sends.

import { execFileSync } from 'node:child_process';
import { readdirSync } from 'node:fs';
import * as path from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';
import { keysFor } from '../src/actions.ts';
import { decorate } from '../src/decorate/index.ts';
import type { Decorations, Span } from '../src/decorations.ts';
import {
  decodeSnapshot,
  isNondisplay,
  type ScreenSnapshot,
  type WireSnapshot,
} from '../src/protocol/index.ts';

const root = fileURLToPath(new URL('../../..', import.meta.url));
const dir = path.join(root, 'test', 'recordings');
const files = (() => {
  try {
    return readdirSync(dir).filter((name) => name.endsWith('.bin'));
  } catch {
    return [];
  }
})();

/** Every screen a recording shows, as the server would send each one. */
function screens(name: string): ScreenSnapshot[] {
  const output = execFileSync(
    'dotnet',
    ['run', '--project', 'server/tools/GreenGlow.Tools', '--', 'snapshots', path.join(dir, name)],
    { cwd: root, encoding: 'utf8', maxBuffer: 256 * 1024 * 1024 },
  );
  return output
    .split('\n')
    .filter(Boolean)
    .map((line) => decodeSnapshot(JSON.parse(line) as WireSnapshot));
}

function checkBounds(snapshot: ScreenSnapshot, decorations: Decorations): void {
  const lines = snapshot.rows + (snapshot.messageLine ? 1 : 0);
  const inBounds = (span: Span) =>
    span.row >= 1 &&
    span.row <= lines &&
    span.column >= 1 &&
    span.length >= 1 &&
    span.column + span.length - 1 <= snapshot.columns;
  for (const { span } of decorations.text) expect(inBounds(span)).toBe(true);
  for (const span of decorations.masks) expect(inBounds(span)).toBe(true);
  for (const { rect } of decorations.boxes) {
    expect(inBounds({ row: rect.row, column: rect.column, length: rect.columns })).toBe(true);
    expect(rect.row + rect.rows - 1).toBeLessThanOrEqual(lines);
  }
}

/**
 * Targets are in bounds, never overlap each other or an input field (a click there must
 * position the cursor), and a key target's guard matches the screen it came from. With a
 * window open, every target is inside it: the screen behind is inert.
 */
function checkTargets(snapshot: ScreenSnapshot, decorations: Decorations): void {
  const window = decorations.boxes.find((box) => box.kind === 'window')?.rect;
  const taken = new Set<number>();
  for (const field of snapshot.fields.filter((f) => !f.format.bypass)) {
    const start = (field.row - 1) * snapshot.columns + field.column - 1;
    for (let i = start; i < start + field.length; i++) taken.add(i);
  }
  for (const target of decorations.targets) {
    const { span } = target;
    expect(span.row).toBeGreaterThanOrEqual(1);
    expect(span.column + span.length - 1).toBeLessThanOrEqual(snapshot.columns);
    for (let c = span.column; c < span.column + span.length; c++) {
      const index = (span.row - 1) * snapshot.columns + c - 1;
      expect(taken.has(index)).toBe(false);
      taken.add(index);
    }
    expect(keysFor(target.action, snapshot)).toBeDefined();
    if (window) {
      expect(span.row).toBeGreaterThan(window.row);
      expect(span.row).toBeLessThan(window.row + window.rows - 1);
      expect(span.column).toBeGreaterThan(window.column + 1);
      expect(span.column + span.length).toBeLessThan(window.column + window.columns - 1);
    }
  }
}

/**
 * Tables: every record is inside the screen and inside the table's stripes, a live table
 * has one hover band per record (an inert one, behind a window, has none), and a table
 * has an id that is its first record's row.
 */
function checkTables(snapshot: ScreenSnapshot, decorations: Decorations): void {
  for (const table of decorations.tables) {
    expect(table.records.length).toBeGreaterThan(0);
    const stripes = decorations.boxes.find((box) => box.id === table.id)?.rect;
    expect(stripes).toBeDefined();
    if (!stripes) continue;
    expect(table.id).toBe(`table:${table.records[0].row}`);
    for (const record of table.records) {
      expect(record.row).toBeGreaterThanOrEqual(Math.max(2, stripes.row));
      expect(record.row + record.rows).toBeLessThanOrEqual(stripes.row + stripes.rows + 1);
      expect(record.row + record.rows - 1).toBeLessThanOrEqual(snapshot.rows);
    }
    const bands = decorations.boxes.filter(
      (box) => box.kind === 'band' && table.records.some((r) => r.row === box.rect.row),
    );
    expect(bands.length).toBe(table.inert ? 0 : table.records.length);
  }
}

describe.skipIf(files.length === 0)('GreenGlow on local recordings', () => {
  it.each(files)('%s decorates every screen', { timeout: 120_000 }, (name) => {
    let slowest = 0;
    for (const snapshot of screens(name)) {
      const started = performance.now();
      const decorations = decorate(snapshot);
      slowest = Math.max(slowest, performance.now() - started);
      checkBounds(snapshot, decorations);
      // Same screen, same decorations.
      expect(JSON.stringify(decorate(snapshot))).toBe(JSON.stringify(decorations));
      // One box per row of every input field, and masks only over nondisplay cells.
      const inputs = snapshot.fields.filter((f) => !f.format.bypass);
      const fieldBoxes = decorations.boxes.filter((box) => box.kind === 'field');
      expect(fieldBoxes.length).toBeGreaterThanOrEqual(inputs.length);
      for (const span of decorations.masks) {
        const index = (span.row - 1) * snapshot.columns + span.column - 1;
        expect(isNondisplay(snapshot.attributes[index])).toBe(true);
      }
      // Box ids are unique, so layer elements are reused correctly.
      const ids = decorations.boxes.map((box) => box.id);
      expect(new Set(ids).size).toBe(ids.length);
      expect(decorations.boxes.filter((box) => box.kind === 'window').length).toBeLessThan(2);
      checkTargets(snapshot, decorations);
      checkTables(snapshot, decorations);
    }
    // Generous: the budget for recognize, decorate and paint is 4 ms.
    expect(slowest).toBeLessThan(50);
  });
});
