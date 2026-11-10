/**
 * Operator error texts, shown on the error line as ACS shows them. 0005 and 0020 match
 * ACS on the test system (30 September 2026); the others are still to be seen there.
 */
const OPERATOR_ERRORS: Record<number, string> = {
  4: 'Entry of data not allowed in this input/output field.',
  5: 'Cursor in protected area of display.',
  7: 'Mandatory data entry field. Must have data entered.',
  8: 'Field requires alphabetic characters.',
  9: 'Field requires numeric characters.',
  10: 'Only characters 0 through 9 allowed.',
  11: 'Key for sign position of field not valid.',
  12: 'No room to insert data.',
  14: 'Mandatory fill field. Must fill to exit.',
  16: 'Field Minus key not valid in field.',
  18: 'The key used to exit field not valid.',
  19: 'Duplicate key or Field Mark key not allowed in field.',
  20: 'Enter key not allowed in field.',
  21: 'Mandatory enter field must have data entered.',
  26: '0-9 required for last position of numeric-only field.',
};

/** The text for an operator error code, as ACS shows it on the error line. */
export function operatorErrorText(code: number): string {
  return OPERATOR_ERRORS[code] ?? `Keyboard error ${String(code).padStart(4, '0')}.`;
}
