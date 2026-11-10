// Synthetic screens for web tests: placeholder text only, never a real system's. The page
// renders the server's snapshots, so tests build ScreenSnapshot objects directly. Screens
// are written with 5250 orders (SBA, SF, IC, SOH, attribute bytes and EBCDIC text), which a
// small interpreter puts into the buffer as a WRITE TO DISPLAY would; the rest (errors,
// typing, the keyboard) are plain methods. The engine itself is tested in server/tests.

import {
  attributesInEffect,
  decodeFieldFormat,
  encodeEbcdic,
  NORMAL_ATTRIBUTE,
  type ErrorState,
  type ScreenSnapshot,
} from '../src/protocol/index.ts';

/** Orders and data, possibly nested; flattened before they are written. */
export type Orders = readonly (number | readonly number[])[];

export const ESC = 0x04;
const SBA = 0x11;
const IC = 0x13;
const SF = 0x1d;
const SOH = 0x01;
/** READ MDT FIELDS: the READ a screen unlocked by show() waits on. */
const READ_MDT = 0x52;
const FFW_BYPASS = 0x2000;
const FFW_MODIFIED = 0x0800;

export const text = (s: string) => [...encodeEbcdic(s)];
export const sba = (row: number, column: number) => [SBA, row, column];
/** Insert Cursor: where the cursor goes when the keyboard unlocks. */
export const ic = (row: number, column: number) => [IC, row, column];
/** SF with a field format word: attribute, then a 2-byte length. */
export const sf = (ffw: number, attribute: number, length: number) => [
  SF,
  ffw >> 8,
  ffw & 0xff,
  attribute,
  length >> 8,
  length & 0xff,
];
/** Start of Header with the error row in byte 4 and no PF key masks. */
export const soh = (errorRow: number) => [SOH, 0x07, 0x00, 0x00, 0x00, errorRow, 0, 0, 0];

/** Field format words: FFW bit pattern 01, then the rules. */
export const FFW = {
  alpha: 0x4000,
  bypass: 0x6000,
  monocase: 0x4020,
  numeric: 0x4300,
  signed: 0x4700,
  mandatory: 0x4008,
  rightZero: 0x4305,
  autoEnter: 0x4080,
} as const;

interface Field {
  /** Buffer index of the first data position; the attribute is one before it. */
  start: number;
  length: number;
  ffw: number;
  fcws: number[];
  attribute: number;
}

/** One display: a buffer, a format table, a cursor and a keyboard. */
export class TestScreen {
  readonly wide: boolean;
  rows = 24;
  columns = 80;
  buffer = new Uint8Array(0);
  messageLine = new Uint8Array(0);
  fields: Field[] = [];
  cursor = 0;
  insertCursor: number | undefined;
  errorRow = 24;
  error: ErrorState | undefined;
  /** Input inhibited; an error shows over it, and Reset leaves it locked. */
  locked = true;
  pendingRead: number | undefined;
  typeAhead = 0;
  #address = 0;
  #savedErrorLine: Uint8Array | undefined;

  /** A blank, locked display, as before a session's first screen. */
  constructor(options: { wide?: boolean } = {}) {
    this.wide = options.wide ?? false;
    this.clear();
    this.buffer[1] = 0;
  }

  /** CLEAR UNIT (24 x 80) or CLEAR UNIT ALTERNATE (27 x 132). */
  clear(): this {
    this.rows = this.wide ? 27 : 24;
    this.columns = this.wide ? 132 : 80;
    this.buffer = new Uint8Array(this.rows * this.columns);
    this.messageLine = new Uint8Array(this.columns);
    this.buffer[1] = NORMAL_ATTRIBUTE;
    this.fields = [];
    this.pendingRead = undefined;
    this.#address = 0;
    this.cursor = 0;
    this.insertCursor = undefined;
    this.errorRow = this.rows;
    this.error = undefined;
    this.#savedErrorLine = undefined;
    return this;
  }

  /** Writes orders and data, as a WRITE TO DISPLAY does, without unlocking the keyboard. */
  write(orders: Orders): this {
    const bytes = (orders as unknown[]).flat(Infinity) as number[];
    let i = 0;
    let offScreen = false;
    while (i < bytes.length) {
      const byte = bytes[i++];
      switch (byte) {
        case SBA:
        case IC: {
          const index = this.#index(bytes[i], bytes[i + 1]);
          i += 2;
          if (byte === IC) {
            if (index !== undefined) this.insertCursor = index;
          } else {
            offScreen = index === undefined;
            if (index !== undefined) this.#address = index;
          }
          break;
        }
        case SOH: {
          const header = bytes.slice(i + 1, i + 1 + bytes[i]);
          i += 1 + bytes[i];
          // SOH clears the format table and locks the keyboard.
          this.fields = [];
          this.locked = true;
          if (header[3] >= 1 && header[3] <= this.rows + 1) this.errorRow = header[3];
          break;
        }
        case SF: {
          let ffw: number | undefined;
          const fcws: number[] = [];
          if ((bytes[i] & 0xc0) === 0x40) {
            ffw = (bytes[i] << 8) | bytes[i + 1];
            i += 2;
            while ((bytes[i] & 0x80) !== 0) {
              fcws.push((bytes[i] << 8) | bytes[i + 1]);
              i += 2;
            }
          }
          const attribute = bytes[i];
          const length = (bytes[i + 1] << 8) | bytes[i + 2];
          i += 3;
          if (offScreen) break;
          this.buffer[this.#address] = attribute;
          const start = (this.#address + 1) % this.buffer.length;
          this.#address = start;
          if (ffw === undefined) break;
          this.locked = true;
          const existing = this.fields.find((f) => f.start === start);
          if (existing) {
            existing.ffw = ffw;
            existing.attribute = attribute;
          } else {
            this.fields.push({ start, length, ffw, fcws, attribute });
            this.fields.sort((a, b) => a.start - b.start);
          }
          break;
        }
        default:
          if (offScreen) break;
          this.buffer[this.#address] = byte;
          this.#address = (this.#address + 1) % this.buffer.length;
      }
    }
    return this;
  }

  /** Unlocks the keyboard for a READ MDT FIELDS and homes the cursor. */
  unlock(): this {
    this.locked = false;
    this.pendingRead = READ_MDT;
    this.cursor =
      this.insertCursor ?? this.fields.find((f) => (f.ffw & FFW_BYPASS) === 0)?.start ?? 0;
    return this;
  }

  lock(): this {
    this.locked = true;
    return this;
  }

  /**
   * WRITE ERROR CODE, or WRITE ERROR CODE TO WINDOW with `window` columns: the error line
   * (or that part of it) is blanked, then the attribute (unless null) and the text go there.
   */
  hostError(
    message: string,
    options: { attribute?: number | null; window?: [number, number] } = {},
  ): this {
    const { attribute = 0x22, window } = options;
    const first = window ? window[0] - 1 : 0;
    const end = window ? window[1] : this.columns;
    const bytes = [...(attribute === null ? [] : [attribute]), ...text(message)];
    this.#savedErrorLine ??= this.#readErrorLine();
    const line = this.#readErrorLine();
    line.fill(0, first, end);
    line.set(bytes.slice(0, end - first), first);
    this.#writeErrorLine(line);
    if (!this.error?.help) this.locked = false;
    this.error = { kind: 'host', code: 0, help: false };
    this.typeAhead = 0;
    return this;
  }

  /** An operator error, such as 0009 for a letter in a numeric field. */
  operatorError(code: number): this {
    this.error = { kind: 'operator', code, help: false };
    return this;
  }

  /** Reset: clears an error and restores the error line. A locked keyboard stays locked. */
  reset(): this {
    if (this.#savedErrorLine) this.#writeErrorLine(this.#savedErrorLine);
    this.#savedErrorLine = undefined;
    this.error = undefined;
    return this;
  }

  cursorTo(row: number, column: number): this {
    this.cursor = this.#index(row, column) ?? this.cursor;
    return this;
  }

  /** Types characters at the cursor, which moves on after each. */
  type(characters: string): this {
    for (const byte of encodeEbcdic(characters)) {
      this.buffer[this.cursor] = byte;
      const field = this.fields.find(
        (f) => this.cursor >= f.start && this.cursor < f.start + f.length,
      );
      if (field) field.ffw |= FFW_MODIFIED;
      this.cursor = (this.cursor + 1) % this.buffer.length;
    }
    return this;
  }

  get screen(): ScreenSnapshot {
    const position = (index: number) => ({
      row: Math.floor(index / this.columns) + 1,
      column: (index % this.columns) + 1,
    });
    const keyboard = this.error ? 'error' : this.locked ? 'locked' : 'unlocked';
    return {
      rows: this.rows,
      columns: this.columns,
      buffer: this.buffer.slice(),
      attributes: attributesInEffect(this.buffer),
      fields: this.fields.map((f) => ({
        ...position(f.start),
        length: f.length,
        ffw: f.ffw,
        fcws: [...f.fcws],
        attribute: f.attribute,
        format: decodeFieldFormat(f.ffw),
      })),
      cursor: position(this.cursor),
      keyboard,
      keyboardLocked: keyboard !== 'unlocked',
      error: this.error ? { ...this.error } : undefined,
      insertMode: false,
      fieldExitRequired: false,
      typeAhead: this.typeAhead,
      messageWaiting: false,
      errorRow: this.errorRow,
      messageLine: this.errorRow > this.rows ? this.messageLine.slice() : undefined,
      pendingRead: this.pendingRead,
    };
  }

  #index(row: number, column: number): number | undefined {
    if (row < 1 || row > this.rows || column < 1 || column > this.columns) return undefined;
    return (row - 1) * this.columns + (column - 1);
  }

  #readErrorLine(): Uint8Array {
    if (this.errorRow > this.rows) return this.messageLine.slice();
    const start = (this.errorRow - 1) * this.columns;
    return this.buffer.slice(start, start + this.columns);
  }

  #writeErrorLine(line: Uint8Array): void {
    if (this.errorRow > this.rows) this.messageLine.set(line.subarray(0, this.columns));
    else this.buffer.set(line.subarray(0, this.columns), (this.errorRow - 1) * this.columns);
  }
}

/**
 * A screen: cleared (27 x 132 when wide), written, and unlocked for a READ MDT FIELDS, so
 * the cursor goes to the IC address or the first input field.
 */
export function show(orders: Orders, options: { wide?: boolean } = {}): TestScreen {
  return new TestScreen(options).clear().write(orders).unlock();
}
