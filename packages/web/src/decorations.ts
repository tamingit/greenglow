// What a look draws on top of the text: extra classes on cells, masks for nondisplay
// input, boxes under the text, and targets a click can act on. Decorations come from pure
// functions of a snapshot; they never change the screen, and a target's action is only
// ever keys a user could type.

import type { AidKeyName } from './protocol/index.ts';

/** Cells on one row; 1-based like the engine's positions. */
export interface Span {
  readonly row: number;
  readonly column: number;
  readonly length: number;
}

export interface Rect {
  readonly row: number;
  readonly column: number;
  readonly rows: number;
  readonly columns: number;
}

/** A class added to cells after the attribute's classes. */
export interface TextStyle {
  readonly span: Span;
  readonly className: string;
}

export type BoxKind =
  | 'window'
  | 'field'
  | 'message'
  | 'header'
  | 'chip'
  | 'pill'
  | 'keycap'
  | 'table'
  | 'heading'
  | 'band'
  | 'pager'
  | 'search';

/** Drawn under the text. The id is stable across screens, so the element is reused. */
export interface Box {
  readonly id: string;
  readonly kind: BoxKind;
  readonly rect: Rect;
  readonly modifiers: readonly string[];
}

/** Text that must still be on the live screen, so a click on a stale paint does nothing. */
export interface TextGuard {
  readonly span: Span;
  readonly text: string;
}

export type Action =
  /** Presses an AID key that the screen itself names, such as F3 in "F3=Exit". */
  | { readonly type: 'key'; readonly key: AidKeyName; readonly guard: TextGuard }
  /** Copies text from the screen to the clipboard; nothing goes to the host. */
  | { readonly type: 'copy'; readonly text: string };

/**
 * A place a click acts on. It has no element of its own: the pointer finds it by cell, so
 * focus never leaves the terminal.
 */
export interface Target {
  readonly id: string;
  readonly span: Span;
  readonly action: Action;
  /** What the click does, shown as the tooltip, for example "Exit (F3)". */
  readonly label: string;
  /** The box that shows hover and press. */
  readonly box: string;
}

/**
 * A recognized subfile. A hovered record lights its band (the `band` box with the id
 * `band:<first row>`); a table behind a window is `inert` and lights nothing.
 */
export interface Table {
  readonly id: string;
  readonly inert: boolean;
  /** Each record's rows, in screen order. */
  readonly records: readonly Rect[];
}

export interface Decorations {
  readonly text: readonly TextStyle[];
  /**
   * Nondisplay input fields: typed characters show as bullets. Only positions are given;
   * the characters themselves never leave the buffer.
   */
  readonly masks: readonly Span[];
  readonly boxes: readonly Box[];
  readonly targets: readonly Target[];
  readonly tables: readonly Table[];
  /** The rules of the input field holding the cursor, for the status line. */
  readonly fieldInfo?: string;
}
