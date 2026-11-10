// Legends of codes and labels, such as "F3=Exit   F12=Cancel" or "2=Edit 4=Delete". Items
// are separated by two or more spaces, or by one space when the next item starts there.

export interface CodeItem {
  readonly code: string;
  readonly label: string;
  /** 1-based column of the code, and the column just past the label. */
  readonly column: number;
  readonly end: number;
}

export interface Tokens {
  readonly items: CodeItem[];
  /** The text with every item blanked: what the legend doesn't explain. */
  readonly rest: string;
}

/** Function keys F1–F24 and the named keys a legend may show. */
export const KEY_CODES = String.raw`F(?:2[0-4]|1\d|[1-9])|Enter|ENTER|PageDown|Pagedown|PAGEDOWN|PgDn|Rollup|RollUp|ROLLUP|PageUp|Pageup|PAGEUP|PgUp|Rolldown|RollDown|ROLLDOWN|Help|HELP|Print|PRINT`;

/**
 * Finds the items in a line. A code starts the line or follows a space, comma, "(", ";"
 * or "|", and is followed by "=" (one space allowed each side), or by "-" and a letter
 * when `dash` is set ("F12-Return").
 */
export function tokenize(text: string, codes: string, options: { dash?: boolean } = {}): Tokens {
  const after = options.dash ? String.raw`(?: ?= ?|-(?=[A-Za-z]))` : ' ?= ?';
  const pattern = new RegExp(String.raw`(?<=^|[\s,(;|])(${codes})${after}`, 'g');
  const starts = Array.from(text.matchAll(pattern), (match) => ({
    index: match.index,
    code: match[1],
    labelStart: match.index + match[0].length,
  }));
  const items: CodeItem[] = [];
  let rest = text;
  starts.forEach((start, i) => {
    const next = starts[i + 1]?.index ?? text.length;
    let labelEnd = next;
    const gap = text.slice(start.labelStart, next).search(/ {2}/);
    if (gap >= 0) labelEnd = start.labelStart + gap;
    const label = text.slice(start.labelStart, labelEnd).replace(/[\s,;|]+$/, '');
    const end = start.labelStart + label.length;
    items.push({ code: start.code, label, column: start.index + 1, end: end + 1 });
    rest = rest.slice(0, start.index) + ' '.repeat(end - start.index) + rest.slice(end);
  });
  return { items, rest };
}
