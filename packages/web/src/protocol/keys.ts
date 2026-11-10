// Keys as the page sends them to the server ({type: 'key', key}). The server accepts only
// the names below (Keyboard.KeyNames in server/src/GreenGlow.Engine/Keyboard.cs).

export const FUNCTION_KEYS = [
  'F1',
  'F2',
  'F3',
  'F4',
  'F5',
  'F6',
  'F7',
  'F8',
  'F9',
  'F10',
  'F11',
  'F12',
  'F13',
  'F14',
  'F15',
  'F16',
  'F17',
  'F18',
  'F19',
  'F20',
  'F21',
  'F22',
  'F23',
  'F24',
] as const;

export type FunctionKeyName = (typeof FUNCTION_KEYS)[number];

/** Keys that send an attention identifier (AID) to the host. */
export type AidKeyName =
  | 'Enter'
  | FunctionKeyName
  | 'RollUp'
  | 'RollDown'
  | 'Help'
  | 'Print'
  | 'Clear'
  | 'RecordBackspace';

export type EditKeyName =
  | 'FieldExit'
  | 'FieldPlus'
  | 'FieldMinus'
  | 'Dup'
  | 'EraseEof'
  | 'EraseInput'
  | 'Insert'
  | 'Delete'
  /** Moves left and deletes the character there, within a field. */
  | 'Backspace'
  | 'Home'
  | 'End'
  | 'Tab'
  | 'Backtab'
  | 'NewLine'
  | 'Left'
  | 'Right'
  | 'Up'
  | 'Down'
  | 'Reset';

export type SignalKeyName = 'Attention' | 'SystemRequest';

export type KeyName = AidKeyName | EditKeyName | SignalKeyName;

export type Key =
  | { type: 'character'; char: string }
  | { type: 'function'; name: KeyName }
  /** Puts the cursor at a position, as a click does. 1-based. */
  | { type: 'cursor'; row: number; column: number };
