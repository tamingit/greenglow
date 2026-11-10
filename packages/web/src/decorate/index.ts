// GreenGlow decorations: the screen's recognized parts turned into what the renderer
// draws. Shared by GreenGlow and Night Shift.

import type { ScreenSnapshot } from '../protocol/index.ts';
import type { Box, Decorations, Table, Target, TextStyle } from '../decorations.ts';
import { recognize } from '../recognize/index.ts';
import { screenView } from '../recognize/view.ts';
import { decorateFields } from './fields.ts';
import { decorateHeader } from './header.ts';
import { decorateKeys } from './keys.ts';
import { decorateMessage } from './message.ts';
import { decorateSubfiles } from './subfile.ts';
import { decorateWindow } from './window.ts';

interface Part {
  readonly boxes: readonly Box[];
  readonly text: readonly TextStyle[];
  readonly targets?: readonly Target[];
  readonly tables?: readonly Table[];
}

/**
 * Classic's hotspots: the function keys the screen names ("F3=Exit"), which a click presses
 * as in ACS, and nothing drawn. Behind an active window there are none, as with the pills.
 */
export function hotspots(snapshot: ScreenSnapshot): Target[] {
  const recognized = recognize(screenView(snapshot));
  return recognized.scopes.flatMap((scope) => decorateKeys(scope).targets);
}

export function decorate(snapshot: ScreenSnapshot): Decorations {
  const view = screenView(snapshot);
  const recognized = recognize(view);
  const fields = decorateFields(view, recognized);
  const parts: Part[] = [
    decorateWindow(view, recognized),
    ...recognized.scopes.flatMap((scope) => [
      decorateHeader(scope),
      decorateKeys(scope),
      decorateSubfiles(scope),
    ]),
    decorateMessage(recognized),
    fields,
  ];
  return {
    text: parts.flatMap((part) => part.text),
    masks: fields.masks,
    boxes: parts.flatMap((part) => part.boxes),
    targets: parts.flatMap((part) => part.targets ?? []),
    tables: parts.flatMap((part) => part.tables ?? []),
    fieldInfo: fields.info,
  };
}
