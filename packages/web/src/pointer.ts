// The mouse on the terminal. Clicks are matched to targets by cell, so no overlay element
// ever takes focus and the keyboard path stays exactly as it is. A click that isn't on a
// target positions the cursor, as it always has; Classic's only targets are its hotspots.

import type { AidKeyName, Key, Position, ScreenSnapshot } from './protocol/index.ts';
import { keysFor, runKeys } from './actions.ts';
import type { Decorations, Rect, Target } from './decorations.ts';
import type { Renderer } from './renderer.ts';
import type { Selection } from './selection.ts';

/** The target under a screen position, if any. */
export function targetAt(
  targets: readonly Target[] | undefined,
  position: Position,
): Target | undefined {
  return targets?.find(
    ({ span }) =>
      span.row === position.row &&
      position.column >= span.column &&
      position.column < span.column + span.length,
  );
}

function inside(rect: Rect, position: Position): boolean {
  return (
    position.row >= rect.row &&
    position.row < rect.row + rect.rows &&
    position.column >= rect.column &&
    position.column < rect.column + rect.columns
  );
}

/** The id of the hover band of the record under a position, if it is in a live table. */
export function bandAt(
  decorations: Decorations | undefined,
  position: Position,
): string | undefined {
  for (const table of decorations?.tables ?? []) {
    if (table.inert) continue;
    const record = table.records.find((rect) => inside(rect, position));
    if (record) return `band:${record.row}`;
  }
  return undefined;
}

/**
 * Turns the mouse wheel into Roll keys, as ACS does: any movement pages, and spinning the
 * wheel keeps paging as fast as the host answers. A roll is sent only when the keyboard
 * is free (so at most one per host wait, never typed ahead), and never closer than
 * `minGapMs` to the one before, which caps a trackpad's flick at a dozen pages a second
 * even when the host answers at once.
 */
export class WheelPager {
  readonly #minGapMs: number;
  #lastRoll = -Infinity;

  constructor(minGapMs = 30) {
    this.#minGapMs = minGapMs;
  }

  /**
   * One wheel event. `delta` is its vertical movement (positive is down); `free` is
   * whether the keyboard is unlocked. Returns the Roll to press, if this event earns one.
   */
  wheel(now: number, delta: number, free: boolean): 'RollUp' | 'RollDown' | undefined {
    if (!free || delta === 0 || now - this.#lastRoll < this.#minGapMs) return undefined;
    this.#lastRoll = now;
    return delta > 0 ? 'RollUp' : 'RollDown';
  }
}

/**
 * Turns a finger swiped up or down the screen into Roll keys, for touch screens, where there
 * is no wheel: a swipe up is Page Down (the list moves up), a swipe down is Page Up. A swipe
 * rolls once it has moved `distance` pixels, mostly vertically; one that keeps going rolls
 * again every `distance` more, while the keyboard is free.
 */
export class SwipePager {
  readonly #distance: number;
  #from: { x: number; y: number } | undefined;

  constructor(distance = 80) {
    this.#distance = distance;
  }

  start(x: number, y: number): void {
    this.#from = { x, y };
  }

  /** The finger moved; returns the Roll to press, if this movement earns one. */
  move(x: number, y: number, free: boolean): 'RollUp' | 'RollDown' | undefined {
    const from = this.#from;
    if (!from) return undefined;
    const dy = from.y - y;
    const dx = from.x - x;
    if (Math.abs(dy) < this.#distance || Math.abs(dy) < 2 * Math.abs(dx)) return undefined;
    this.#from = { x, y };
    if (!free) return undefined;
    return dy > 0 ? 'RollUp' : 'RollDown';
  }

  end(): void {
    this.#from = undefined;
  }
}

export interface PointerHost {
  readonly terminal: HTMLElement;
  readonly renderer: Renderer;
  readonly selection: Selection;
  /** The engine's current screen, for checking a click before it sends anything. */
  live(): ScreenSnapshot | undefined;
  press(key: Key): void;
}

export function attachPointer(host: PointerHost): void {
  const { terminal, renderer } = host;
  /** What the mouse went down on: a target, or one of the status line's key buttons. */
  let down: { target: Target } | { key: AidKeyName; button: HTMLElement } | undefined;
  /** The cell the mouse went down on, while the button is held: where a drag starts. */
  let anchor: Position | undefined;
  let dragging = false;
  /** A plain click moves the cursor on release, so a drag (a selection) leaves it alone. */
  let cursorTo: Position | undefined;
  let hovered: string | undefined;
  let lit: string | undefined;
  const pager = new WheelPager();

  terminal.addEventListener('mousedown', (event) => {
    // The on-screen keyboard's box is inside the terminal: a tap keeps it.
    if (!terminal.contains(document.activeElement)) terminal.focus();
    if (event.button !== 0) return;
    const button = (event.target as Element).closest<HTMLElement>('[data-key]');
    if (button && terminal.contains(button)) {
      event.preventDefault();
      down = { key: button.dataset.key as AidKeyName, button };
      button.classList.add('pressed');
      return;
    }
    const position = renderer.positionAt(event.clientX, event.clientY);
    if (!position) return;
    event.preventDefault();
    // Shift+click moves the far corner of the selection; any other click clears it.
    if (event.shiftKey && host.selection.extend(position)) return;
    host.selection.clear();
    anchor = position;
    const target = targetAt(renderer.targets, position);
    if (target) {
      down = { target };
      renderer.press(target.box);
    } else {
      cursorTo = position;
    }
  });

  // The action happens on release over the same target, so dragging off cancels it.
  window.addEventListener('mouseup', (event) => {
    anchor = undefined;
    const dragged = dragging;
    dragging = false;
    const moveTo = cursorTo;
    cursorTo = undefined;
    if (moveTo && !dragged && event.button === 0) host.press({ type: 'cursor', ...moveTo });
    if (!down) return;
    const was = down;
    down = undefined;
    if ('key' in was) {
      was.button.classList.remove('pressed');
      if (was.button.contains(event.target as Node)) {
        runKeys([{ type: 'function', name: was.key }], host);
      }
      return;
    }
    renderer.press(undefined);
    const position = renderer.positionAt(event.clientX, event.clientY);
    const target = position && targetAt(renderer.targets, position);
    if (target?.id === was.target.id) act(target);
  });

  // A drag that leaves its first cell selects a rectangle, and is no longer a click.
  window.addEventListener('mousemove', (event) => {
    if (!anchor) return;
    if ((event.buttons & 1) === 0) {
      anchor = undefined;
      return;
    }
    const position = renderer.positionAt(event.clientX, event.clientY, true);
    if (!position) return;
    if (!dragging) {
      if (position.row === anchor.row && position.column === anchor.column) return;
      dragging = true;
      if (down && 'target' in down) renderer.press(undefined);
      down = undefined;
    }
    host.selection.drag(anchor, position);
  });

  terminal.addEventListener('mousemove', (event) => {
    const position = renderer.positionAt(event.clientX, event.clientY);
    const band = position && bandAt(renderer.decorations, position);
    if (band !== lit) renderer.light(band);
    lit = band;
    const target = position && targetAt(renderer.targets, position);
    if (target?.id === hovered) return;
    hovered = target?.id;
    renderer.hover(target?.box);
    terminal.classList.toggle('over-target', target !== undefined);
    terminal.title = target?.label ?? '';
  });

  terminal.addEventListener('mouseleave', () => {
    hovered = undefined;
    lit = undefined;
    renderer.light(undefined);
    renderer.hover(undefined);
    terminal.classList.remove('over-target');
    terminal.title = '';
  });

  // The wheel pages the screen wherever it is: Page Down for a turn down, Page Up for a
  // turn up, as in ACS. It is ignored while the keyboard is locked.
  terminal.addEventListener(
    'wheel',
    (event) => {
      event.preventDefault();
      const live = host.live();
      const free = live !== undefined && live.keyboard !== 'locked' && !live.error?.help;
      const roll = pager.wheel(event.timeStamp, event.deltaY, free);
      if (roll) runKeys([{ type: 'function', name: roll }], host);
    },
    { passive: false },
  );

  // On a touch screen a swipe pages, as the wheel does (the terminal takes vertical pans:
  // touch-action in terminal.css).
  const swiper = new SwipePager();
  terminal.addEventListener('pointerdown', (event) => {
    if (event.pointerType === 'touch') swiper.start(event.clientX, event.clientY);
  });
  terminal.addEventListener('pointermove', (event) => {
    if (event.pointerType !== 'touch') return;
    const live = host.live();
    const free = live !== undefined && live.keyboard !== 'locked' && !live.error?.help;
    const roll = swiper.move(event.clientX, event.clientY, free);
    if (roll) runKeys([{ type: 'function', name: roll }], host);
  });
  for (const type of ['pointerup', 'pointercancel'] as const)
    terminal.addEventListener(type, (event) => {
      if (event.pointerType === 'touch') swiper.end();
    });

  function act(target: Target): void {
    const { action } = target;
    if (action.type === 'copy') {
      navigator.clipboard.writeText(action.text).then(
        () => {
          renderer.copied(target.box);
          terminal.title = `Copied ${action.text}`;
        },
        () => (terminal.title = `Copying is blocked here. The ID is ${action.text}.`),
      );
      return;
    }
    const live = host.live();
    const keys = live && keysFor(action, live);
    if (keys) runKeys(keys, host);
  }
}
