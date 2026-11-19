// Tiling: several session tabs on the screen at once, up to 3 wide and 2 tall. The active tab's
// tile holds the page's one terminal (#stage moves into it), so keys, the pointer, selection and
// the Disconnected card work there exactly as they do with one session. The other tiles show
// their sessions' latest screens, painted by a renderer of their own; they take no keys, and a
// click on one makes it the active tab. It is page chrome: nothing here reaches a host.

import type { ScreenSnapshot } from './protocol/index.ts';
import type { Look } from './looks/look.ts';
import { Renderer, type ScreenSize } from './renderer.ts';
import { applyTheme, type Theme } from './themes.ts';

/** How the tiles are laid out: fitted to the sessions and the window, or columns × rows. */
export type TileLayout = 'auto' | '2x1' | '1x2' | '3x1' | '2x2' | '3x2';

export const TILE_LAYOUTS: readonly { id: TileLayout; name: string; title: string }[] = [
  { id: 'auto', name: 'Auto', title: 'Fit the sessions to the window' },
  { id: '2x1', name: '2×1', title: 'Two side by side' },
  { id: '1x2', name: '1×2', title: 'Two, one above the other' },
  { id: '3x1', name: '3×1', title: 'Three side by side' },
  { id: '2x2', name: '2×2', title: 'Two rows of two' },
  { id: '3x2', name: '3×2', title: 'Two rows of three' },
];

/** At most 3 tiles wide and 2 tall. */
export const MAX_COLUMNS = 3;
export const MAX_ROWS = 2;
export const MAX_TILES = MAX_COLUMNS * MAX_ROWS;

export interface Grid {
  columns: number;
  rows: number;
}

/** A 24×80 screen's width and height in font sizes: 0.6 a character, 1.3 a line, 25.85 lines. */
const SCREEN_WIDTH = 80 * 0.6;
const SCREEN_HEIGHT = 25.85 * 1.3;
/** Room a tile takes beyond its screen: its title, the gap, the terminal's padding (px). */
const TILE_EXTRA_Y = 44;
const TILE_EXTRA_X = 24;

/**
 * The grid for `count` sessions in `width` × `height` pixels. A fixed layout is what it says.
 * Auto takes the grid, at most 3 by 2, with a cell for each session (up to 6) that shows their
 * screens biggest, and of those the one with fewest empty cells.
 */
export function gridFor(layout: TileLayout, count: number, width: number, height: number): Grid {
  if (layout !== 'auto') {
    const [columns, rows] = layout.split('x').map(Number);
    return { columns, rows };
  }
  const n = Math.max(1, Math.min(MAX_TILES, count));
  let best: (Grid & { size: number; cells: number }) | undefined;
  for (let rows = 1; rows <= MAX_ROWS; rows++)
    for (let columns = 1; columns <= MAX_COLUMNS; columns++) {
      const cells = columns * rows;
      if (cells < n) continue;
      const size = Math.min(
        (width / columns - TILE_EXTRA_X) / SCREEN_WIDTH,
        (height / rows - TILE_EXTRA_Y) / SCREEN_HEIGHT,
      );
      // A screen only a little bigger isn't worth an empty cell.
      if (
        !best ||
        (cells === best.cells && size > best.size) ||
        (cells < best.cells && size >= best.size * 0.95) ||
        (cells > best.cells && size > best.size * 1.05)
      )
        best = { columns, rows, size, cells };
    }
  return best ? { columns: best.columns, rows: best.rows } : { columns: 1, rows: 1 };
}

/**
 * Which tabs the tiles show, when there are more tabs than cells: the `cells` used most
 * recently (`recent`, latest first), always the active one among them, in tab order (`order`).
 */
export function shownTabs(
  order: readonly number[],
  recent: readonly number[],
  cells: number,
): number[] {
  if (order.length <= cells) return [...order];
  const chosen = new Set(recent.filter((id) => order.includes(id)).slice(0, cells));
  for (const id of order) if (chosen.size < cells) chosen.add(id);
  return order.filter((id) => chosen.has(id));
}

/** What a tile shows for a tab. */
export interface TileView {
  id: number;
  title: string;
  system: string;
  color: string | undefined;
  connected: boolean;
  /** Its session's latest screen, if it has one. */
  screen: ScreenSnapshot | undefined;
}

export interface TileActions {
  /** Make a tab the active one, its tile taking the keys. */
  select(id: number): void;
  /** A new session, from an empty cell. */
  add(): void;
}

/** One cell of the grid, kept and reused: its own renderer paints whichever tab it shows. */
interface Slot {
  root: HTMLElement;
  head: HTMLElement;
  title: HTMLElement;
  system: HTMLElement;
  body: HTMLElement;
  stage: HTMLElement;
  screen: HTMLElement;
  note: HTMLElement;
  add: HTMLButtonElement;
  renderer: Renderer;
  /** The tab it shows, if any. */
  id: number | undefined;
  /** The snapshot waiting for the next frame. */
  latest: ScreenSnapshot | undefined;
}

export class Tiles {
  readonly #workspace: HTMLElement;
  readonly #stage: HTMLElement;
  readonly #grid: HTMLElement;
  readonly #actions: TileActions;
  readonly #slots: Slot[] = [];
  #look: Look;
  #size: ScreenSize = { mode: 'scale' };
  #theme: Theme | undefined;
  #on = false;
  #layout: TileLayout = 'auto';
  #last: { views: readonly TileView[]; active: number; recent: readonly number[] } | undefined;

  /** `workspace` holds `stage`, the active tab's terminal, which moves into its tile. */
  constructor(workspace: HTMLElement, stage: HTMLElement, look: Look, actions: TileActions) {
    this.#workspace = workspace;
    this.#stage = stage;
    this.#look = look;
    this.#actions = actions;
    this.#grid = document.createElement('div');
    this.#grid.className = 'tiles';
    this.#grid.hidden = true;
    workspace.append(this.#grid);
    // Auto fits the grid to the room it has, so a resize may change it.
    new ResizeObserver(() => {
      if (this.#on && this.#layout === 'auto') this.#paint();
    }).observe(workspace);
  }

  get on(): boolean {
    return this.#on;
  }

  get layout(): TileLayout {
    return this.#layout;
  }

  /** Tiles on or off; off, the active tab fills the workspace alone. */
  set on(on: boolean) {
    this.#on = on;
    this.#workspace.toggleAttribute('data-tiled', on);
    this.#paint();
  }

  set layout(layout: TileLayout) {
    this.#layout = layout;
    this.#paint();
  }

  /** The tabs (in order), the active one, and the tab ids used most recently first. */
  show(views: readonly TileView[], active: number, recent: readonly number[]): void {
    this.#last = { views, active, recent };
    this.#paint();
  }

  /** A new screen for a tab that isn't the active one: its tile repaints, if it shows. */
  paint(id: number, snapshot: ScreenSnapshot): void {
    const slot = this.#slots.find((s) => s.id === id && !s.screen.hidden);
    if (!slot) return;
    // At most once a frame, with the latest screen.
    if (slot.latest === undefined)
      requestAnimationFrame(() => {
        if (slot.latest && slot.id === id) slot.renderer.render(slot.latest);
        slot.latest = undefined;
      });
    slot.latest = snapshot;
  }

  setLook(look: Look): void {
    this.#look = look;
    for (const slot of this.#slots) slot.renderer.setLook(look);
  }

  setSize(size: ScreenSize): void {
    this.#size = size;
    for (const slot of this.#slots) slot.renderer.setSize(size);
  }

  setTheme(theme: Theme | undefined): void {
    this.#theme = theme;
    for (const slot of this.#slots) applyTheme(slot.screen, theme);
  }

  #paint(): void {
    const last = this.#last;
    if (!this.#on || !last) {
      this.#grid.hidden = true;
      for (const slot of this.#slots) slot.id = undefined;
      if (this.#stage.parentElement !== this.#workspace) this.#workspace.prepend(this.#stage);
      return;
    }
    const { views, active, recent } = last;
    const grid = gridFor(
      this.#layout,
      views.length,
      this.#workspace.clientWidth,
      this.#workspace.clientHeight,
    );
    const cells = grid.columns * grid.rows;
    // The active tab always has a tile: it holds the terminal.
    const ids = shownTabs(
      views.map((v) => v.id),
      [active, ...recent.filter((id) => id !== active)],
      cells,
    );
    this.#grid.hidden = false;
    this.#grid.style.setProperty('--tile-columns', String(grid.columns));
    this.#grid.style.setProperty('--tile-rows', String(grid.rows));
    while (this.#slots.length < cells) this.#slots.push(this.#slot());
    this.#slots.forEach((slot, i) => {
      slot.root.hidden = i >= cells;
      const view = views.find((v) => v.id === ids[i]);
      this.#fill(slot, i < cells ? view : undefined, view?.id === active);
    });
  }

  /** Shows a tab in a slot: the terminal itself if it is the active tab, else its screen. */
  #fill(slot: Slot, view: TileView | undefined, isActive: boolean): void {
    const was = slot.id;
    slot.id = view?.id;
    slot.root.toggleAttribute('data-empty', !view);
    slot.root.toggleAttribute('data-active', isActive);
    slot.head.hidden = !view;
    slot.add.hidden = view !== undefined;
    if (!view) {
      slot.stage.hidden = true;
      slot.note.hidden = true;
      return;
    }
    slot.title.textContent = view.title;
    slot.system.textContent = view.system;
    slot.root.title = isActive ? '' : `${view.title} · ${view.system}: click to use it`;
    if (view.color) slot.root.style.setProperty('--system-color', view.color);
    else slot.root.style.removeProperty('--system-color');
    slot.root.toggleAttribute('data-colored', view.color !== undefined);
    if (isActive) {
      slot.stage.hidden = true;
      slot.note.hidden = true;
      if (this.#stage.parentElement !== slot.body) slot.body.append(this.#stage);
      return;
    }
    const screen = view.connected ? view.screen : undefined;
    slot.stage.hidden = screen === undefined;
    slot.note.hidden = screen !== undefined;
    slot.note.textContent = view.connected ? 'Connecting…' : 'Not connected';
    if (screen && (was !== view.id || slot.latest === undefined)) slot.renderer.render(screen);
  }

  #slot(): Slot {
    const root = element('div', 'tile');
    const head = element('div', 'tile-head');
    const title = element('span', 'tile-title');
    const system = element('span', 'tile-system');
    head.append(title, system);
    const body = element('div', 'tile-body');
    const stage = element('div', 'tile-stage');
    const screen = element('div', 'tile-screen');
    // The other tiles are pictures of their sessions; the tab strip is how keys reach them.
    screen.setAttribute('aria-hidden', 'true');
    stage.append(screen);
    const note = element('p', 'tile-note');
    const add = document.createElement('button');
    add.type = 'button';
    add.className = 'tile-add';
    add.tabIndex = -1;
    add.textContent = 'New session';
    add.title = 'New session (Alt+N)';
    add.addEventListener('click', () => this.#actions.add());
    body.append(stage, note, add);
    root.append(head, body);
    this.#grid.append(root);
    const renderer = new Renderer(screen, stage, this.#look);
    renderer.setSize(this.#size);
    applyTheme(screen, this.#theme);
    const slot: Slot = {
      root,
      head,
      title,
      system,
      body,
      stage,
      screen,
      note,
      add,
      renderer,
      id: undefined,
      latest: undefined,
    };
    // A press anywhere on another session's tile makes it the active tab, and nothing more:
    // the press isn't passed on, and the focus stays on the terminal until it moves there.
    root.addEventListener('mousedown', (event) => {
      if (event.button !== 0 || slot.id === undefined || root.hasAttribute('data-active')) return;
      event.preventDefault();
      this.#actions.select(slot.id);
    });
    return slot;
  }
}

function element(tag: string, className: string): HTMLElement {
  const node = document.createElement(tag);
  node.className = className;
  return node;
}
