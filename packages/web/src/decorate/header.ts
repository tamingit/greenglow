// Row 1 as a header band, with the program or format ID as a chip that copies it (for a
// Helpdesk ticket) and the title in bold. Behind an active window it is drawn inert.

import type { Box, Target, TextStyle } from '../decorations.ts';
import type { Scope } from '../recognize/index.ts';

export function decorateHeader(scope: Scope): {
  boxes: Box[];
  text: TextStyle[];
  targets: Target[];
} {
  const { header, inert } = scope;
  if (!header) return { boxes: [], text: [], targets: [] };
  const modifiers = inert ? ['inert'] : [];
  const boxes: Box[] = [
    {
      id: 'header',
      kind: 'header',
      rect: { row: 1, column: 1, rows: 1, columns: scope.view.columns },
      modifiers,
    },
  ];
  const text: TextStyle[] = [];
  const targets: Target[] = [];
  if (header.id) {
    const { span } = header.id;
    boxes.push({
      id: 'chip:id',
      kind: 'chip',
      rect: { row: 1, column: span.column, rows: 1, columns: span.length },
      modifiers,
    });
    text.push({ span, className: 'g-id' });
    if (!inert) {
      targets.push({
        id: 'copy:id',
        span,
        action: { type: 'copy', text: header.id.text },
        label: `Copy ${header.id.text}`,
        box: 'chip:id',
      });
    }
  }
  if (header.title) text.push({ span: header.title, className: 'g-title' });
  return { boxes, text, targets };
}
