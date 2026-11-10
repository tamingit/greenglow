// Recognizers: pure functions from the screen to what it means. They use only what the
// IBM i sends, and draw nothing when unsure.
//
// A window splits the screen into scopes: its inside, and the screen around it. The same
// recognizers run once per scope, each on a view that shows only that scope, so a legend
// inside a window is found the way one at the bottom of the screen is. Only the active
// window is live; what is found around it is shown but does nothing. Within a scope, each
// row belongs to the first recognizer that claims it, so a legend on the error row is not
// also a message.

import { recognizeHeader, type Header } from './header.ts';
import { recognizeKeys, type KeyLegend } from './keys.ts';
import { recognizeMessage, type Message } from './message.ts';
import { recognizeSubfiles, type Subfile } from './subfile.ts';
import { scopedView, type ScreenView } from './view.ts';
import { activeWindow, recognizeWindows, type ScreenWindow } from './window.ts';

export interface Scope {
  readonly view: ScreenView;
  /** Shown but not live: the screen behind an active window. */
  readonly inert: boolean;
  readonly keys: readonly KeyLegend[];
  readonly subfiles: readonly Subfile[];
  readonly header: Header | undefined;
}

export interface Recognized {
  /** The window the operator is working in, if the screen shows one. */
  readonly window: ScreenWindow | undefined;
  /** The screen (around the window, if there is one), then the window's inside. */
  readonly scopes: readonly Scope[];
  readonly message: Message | undefined;
}

/** Each recognizer takes the rows it owns before the next one looks. */
function scopeParts(view: ScreenView, claimed: Set<number>) {
  const keys = recognizeKeys(view, claimed);
  const subfiles = recognizeSubfiles(view, claimed);
  return { keys, subfiles, header: recognizeHeader(view, claimed) };
}

export function recognize(view: ScreenView): Recognized {
  const window = activeWindow(view, recognizeWindows(view));
  const views = window
    ? [
        { view: scopedView(view, 'screen', view.bounds, [window.border]), inert: true },
        { view: scopedView(view, 'window', window.content), inert: false },
      ]
    : [{ view, inert: false }];
  const claimed = views.map(() => new Set<number>());
  const scopes = views.map(({ view: scoped, inert }, i) => ({
    view: scoped,
    inert,
    ...scopeParts(scoped, claimed[i]),
  }));
  // The message belongs to the whole screen: the window's inside is read first, then the
  // screen around it (a window without a message line leaves messages on the screen's).
  let message: Message | undefined;
  for (let i = scopes.length - 1; i >= 0 && !message; i--) {
    message = recognizeMessage(scopes[i].view, claimed[i]);
  }
  return { window, scopes, message };
}
