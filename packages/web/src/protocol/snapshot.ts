// The screen as the server sends it: the C# engine's ScreenSnapshot (server/src/
// GreenGlow.Engine/Screen.cs), serialized as JSON with camelCase names. Byte arrays arrive
// as base64 and absent values as null; decodeSnapshot turns them into the shapes below.

export interface Position {
  /** 1-based. */
  row: number;
  /** 1-based. */
  column: number;
}

export type FieldShift =
  | 'alpha-shift'
  | 'alpha-only'
  | 'numeric-shift'
  | 'numeric-only'
  | 'katakana-shift'
  | 'digits-only'
  | 'io'
  | 'signed-numeric';

export type FieldAdjust = 'none' | 'right-zero' | 'right-blank' | 'mandatory-fill';

/** The field format word, decoded (table 15-6). */
export interface FieldFormat {
  bypass: boolean;
  dupEnable: boolean;
  modified: boolean;
  shift: FieldShift;
  autoEnter: boolean;
  fieldExitRequired: boolean;
  monocase: boolean;
  mandatoryEnter: boolean;
  adjust: FieldAdjust;
}

export interface SnapshotField extends Position {
  length: number;
  ffw: number;
  fcws: readonly number[];
  /** The leading field attribute (0x20–0x3F). */
  attribute: number;
  format: FieldFormat;
}

/**
 * Unlocked: the operator can type. Locked: input inhibited while the host works.
 * Error: an operator error or a host WRITE ERROR CODE, cleared by Reset or, as in ACS, by
 * any key except Help, Attn and SysReq.
 */
export type KeyboardState = 'unlocked' | 'locked' | 'error';

export interface ErrorState {
  /** An operator (keying) error, or an error the host wrote with WRITE ERROR CODE. */
  kind: 'operator' | 'host';
  /** The operator error code as its four decimal digits read as a number (0005 is 5); 0 for host errors. */
  code: number;
  /** Help has been pressed (post-help error state): only Attn works until the host answers. */
  help: boolean;
}

export interface ScreenSnapshot {
  readonly rows: number;
  readonly columns: number;
  /** Regeneration buffer, row by row: EBCDIC characters, attribute bytes (0x20–0x3F) and nulls. */
  readonly buffer: Uint8Array;
  /** The field attribute in effect at each position: an attribute lasts until the next one. */
  readonly attributes: Uint8Array;
  readonly fields: readonly SnapshotField[];
  readonly cursor: Position;
  readonly keyboard: KeyboardState;
  /** True unless the keyboard is unlocked (input inhibited). */
  readonly keyboardLocked: boolean;
  readonly error: Readonly<ErrorState> | undefined;
  readonly insertMode: boolean;
  /** The cursor waits on the last position of a field that needs a field exit key. */
  readonly fieldExitRequired: boolean;
  /** Keystrokes waiting for the keyboard to unlock. */
  readonly typeAhead: number;
  readonly messageWaiting: boolean;
  /** Where error messages go. It can be one row below the screen (a separate message line). */
  readonly errorRow: number;
  /** The separate message line, when the error row is below the screen. */
  readonly messageLine: Uint8Array | undefined;
  /** A READ command waiting for the user to press an AID key, if any. */
  readonly pendingRead: number | undefined;
}

/** A snapshot as JSON: byte arrays are base64, and absent values are null. */
export type WireSnapshot = Omit<
  ScreenSnapshot,
  'buffer' | 'attributes' | 'messageLine' | 'error' | 'pendingRead'
> & {
  buffer: string;
  attributes: string;
  messageLine: string | null;
  error: ScreenSnapshot['error'] | null;
  pendingRead: number | null;
};

export function decodeSnapshot(wire: WireSnapshot): ScreenSnapshot {
  return {
    ...wire,
    buffer: base64(wire.buffer),
    attributes: base64(wire.attributes),
    messageLine: wire.messageLine === null ? undefined : base64(wire.messageLine),
    error: wire.error ?? undefined,
    pendingRead: wire.pendingRead ?? undefined,
  };
}

function base64(text: string): Uint8Array {
  return Uint8Array.from(atob(text), (c) => c.charCodeAt(0));
}

const SHIFTS: FieldShift[] = [
  'alpha-shift',
  'alpha-only',
  'numeric-shift',
  'numeric-only',
  'katakana-shift',
  'digits-only',
  'io',
  'signed-numeric',
];

/** Decodes a field format word, as the engine does for SnapshotField.format. */
export function decodeFieldFormat(ffw: number): FieldFormat {
  const adjust = ffw & 0x0007;
  return {
    bypass: (ffw & 0x2000) !== 0,
    dupEnable: (ffw & 0x1000) !== 0,
    modified: (ffw & 0x0800) !== 0,
    shift: SHIFTS[(ffw >> 8) & 0x07],
    autoEnter: (ffw & 0x0080) !== 0,
    fieldExitRequired: (ffw & 0x0040) !== 0,
    monocase: (ffw & 0x0020) !== 0,
    mandatoryEnter: (ffw & 0x0008) !== 0,
    adjust:
      adjust === 5
        ? 'right-zero'
        : adjust === 6
          ? 'right-blank'
          : adjust === 7
            ? 'mandatory-fill'
            : 'none',
  };
}
