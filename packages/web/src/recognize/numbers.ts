// Amounts in a line of text, such as "8,461.10" and the credit "312.40-". An amount has a
// decimal point or digit groups, so identifiers, dates and phone numbers are left alone.

export interface Amount {
  /** 1-based column of the first character, and the characters it takes (sign included). */
  readonly column: number;
  readonly length: number;
  /** Shown with a minus sign, trailing as 5250 programs print it, or leading. */
  readonly credit: boolean;
}

const AMOUNT = /(?<![\w.,/:-])(-?)(\d{1,3}(?:,\d{3})+(?:\.\d+)?|\d+\.\d+)(-?)(?![\w.,/:])/g;

export function findAmounts(line: string): Amount[] {
  return Array.from(line.matchAll(AMOUNT), (match) => ({
    column: match.index + 1,
    length: match[0].length,
    credit: match[1] === '-' || match[3] === '-',
  }));
}
