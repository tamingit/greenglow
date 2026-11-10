// The message line as a banner, pinned where the host wrote it, for as long as it's there:
// across the row, or across a window's inside.

import type { Box, TextStyle } from '../decorations.ts';
import type { Recognized } from '../recognize/index.ts';

export function decorateMessage(recognized: Recognized): { boxes: Box[]; text: TextStyle[] } {
  const message = recognized.message;
  if (!message) return { boxes: [], text: [] };
  const modifiers: string[] = [message.kind];
  const { band } = message;
  // The icon takes the band's first column, so only when the text leaves it free.
  if (message.column > band.column) modifiers.push('icon');
  if (message.more) modifiers.push('more');
  return {
    boxes: [
      {
        id: 'message',
        kind: 'message',
        rect: { row: band.row, column: band.column, rows: 1, columns: band.length },
        modifiers,
      },
    ],
    text: [
      {
        span: { row: message.row, column: message.column, length: message.length },
        className: 'g-message',
      },
    ],
  };
}
