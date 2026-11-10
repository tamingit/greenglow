// Boxes drawn under the text, positioned on the grid by custom properties (the CSP
// forbids style attributes). Elements are reused by id, so a keystroke that only moves
// the focus ring touches two elements.

import type { Box } from './decorations.ts';

export interface LayerPlan {
  readonly add: Box[];
  readonly update: Box[];
  readonly remove: string[];
}

/** What a box looks like, so an unchanged box is left alone. */
export function signature(box: Box): string {
  const { kind, rect, modifiers } = box;
  return `${kind}|${rect.row},${rect.column},${rect.rows},${rect.columns}|${modifiers.join(' ')}`;
}

/** The changes that turn the boxes painted now into the next ones. */
export function planLayer(painted: ReadonlyMap<string, string>, next: readonly Box[]): LayerPlan {
  const plan: LayerPlan = { add: [], update: [], remove: [] };
  const ids = new Set<string>();
  for (const box of next) {
    ids.add(box.id);
    const before = painted.get(box.id);
    if (before === undefined) plan.add.push(box);
    else if (before !== signature(box)) plan.update.push(box);
  }
  for (const id of painted.keys()) if (!ids.has(id)) plan.remove.push(id);
  return plan;
}

export class Layer {
  readonly #element: HTMLElement;
  readonly #elements = new Map<string, HTMLElement>();
  readonly #painted = new Map<string, string>();
  /** States shown on one box at a time, such as hover: state class to box id. */
  readonly #states = new Map<string, string>();

  /** Puts the layer first in `grid`, so it sits under the text. */
  constructor(grid: HTMLElement, className: string) {
    this.#element = document.createElement('div');
    this.#element.className = className;
    this.#element.setAttribute('aria-hidden', 'true');
    grid.prepend(this.#element);
  }

  paint(boxes: readonly Box[]): void {
    const plan = planLayer(this.#painted, boxes);
    for (const id of plan.remove) {
      this.#elements.get(id)?.remove();
      this.#elements.delete(id);
      this.#painted.delete(id);
    }
    for (const box of plan.add) {
      const node = document.createElement('div');
      this.#element.append(node);
      this.#elements.set(box.id, node);
    }
    for (const box of [...plan.add, ...plan.update]) {
      const node = this.#elements.get(box.id);
      if (!node) continue;
      node.className = ['box', box.kind, ...box.modifiers].join(' ');
      for (const [state, id] of this.#states) if (id === box.id) node.classList.add(state);
      node.style.setProperty('--r', String(box.rect.row));
      node.style.setProperty('--c', String(box.rect.column));
      node.style.setProperty('--w', String(box.rect.columns));
      node.style.setProperty('--h', String(box.rect.rows));
      this.#painted.set(box.id, signature(box));
    }
  }

  /** Shows a state (hover, pressed, copied) on one box, or on none. */
  setState(state: string, id: string | undefined): void {
    const before = this.#states.get(state);
    if (before === id) return;
    if (before !== undefined) this.#elements.get(before)?.classList.remove(state);
    if (id === undefined) {
      this.#states.delete(state);
    } else {
      this.#states.set(state, id);
      this.#elements.get(id)?.classList.add(state);
    }
  }

  remove(): void {
    this.#element.remove();
  }
}
