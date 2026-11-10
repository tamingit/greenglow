// The renderer: the regeneration buffer on a row/column grid, coloured by field attribute
// as a colour 5250 shows it (SC30-3533-04 table 15-10), with the cursor, the separate
// message line and an ACS-style status line. A look with decorations (GreenGlow) adds
// classes and masks to the text and boxes under it; Classic has none.

import type { Position, ScreenSnapshot } from './protocol/index.ts';
import type { Decorations, Rect, Target } from './decorations.ts';
import { Layer } from './layers.ts';
import type { Look } from './looks/look.ts';
import { operatorErrorText } from './messages.ts';
import { rowRuns, rowStyles, screenLines } from './text.ts';

/** ACS's rule line: a crosshair through the cursor, or only its row or its column. */
export type RuleLine = 'off' | 'crosshair' | 'horizontal' | 'vertical';

/**
 * How big the screen is: scaled to fit the space keeping its shape, stretched to fill it
 * (the characters widen or narrow), or a fixed font size, scrolling when it doesn't fit.
 */
export type ScreenSize =
  { mode: 'scale' } | { mode: 'stretch' } | { mode: 'fixed'; fontSize: number };

export class Renderer {
  readonly #root: HTMLElement;
  readonly #stage: HTMLElement;
  readonly #grid: HTMLElement;
  readonly #cursor: HTMLElement;
  readonly #status: HTMLElement;
  #rows: HTMLElement[] = [];
  #rowKeys: string[] = [];
  #columns = 80;
  #lines = 24;
  #charRatio = 0.6;
  #size: ScreenSize = { mode: 'scale' };
  #look: Look;
  #last: ScreenSnapshot | undefined;
  #underlay: Layer | undefined;
  #decorations: Decorations | undefined;
  #targets: readonly Target[] = [];
  /** Enter and the Roll keys as buttons in the status line, for looks that decorate. */
  #keys: HTMLElement | undefined;
  #copiedTimer: ReturnType<typeof setTimeout> | undefined;
  /** The highlight of the selected rectangle; it exists only while there is one. */
  #selection: HTMLElement | undefined;

  /** `stage` is the space the screen may fill; its size comes from the page layout. */
  constructor(root: HTMLElement, stage: HTMLElement, look: Look) {
    this.#root = root;
    this.#stage = stage;
    this.#look = look;
    root.classList.add('terminal', ...look.classes);
    this.#grid = element('div', 'grid');
    this.#cursor = element('div', 'cursor');
    this.#status = element('div', 'status');
    // The rule line through the cursor, as ACS draws it when asked (setRuleLine).
    this.#grid.append(element('div', 'rule-row'), element('div', 'rule-column'), this.#cursor);
    root.replaceChildren(this.#grid, this.#status);
    this.#syncLayers();
    new ResizeObserver(() => this.#fit()).observe(stage);
    window.addEventListener('resize', () => this.#fit());
    document.addEventListener('fullscreenchange', () => this.#fit());
  }

  /** The rule line through the cursor: none, both lines, or one of them. */
  setRuleLine(style: RuleLine): void {
    if (style === 'off') delete this.#root.dataset.ruleLine;
    else this.#root.dataset.ruleLine = style;
  }

  /** Scale, stretch or a fixed font size; the screen refits at once. */
  setSize(size: ScreenSize): void {
    this.#size = size;
    this.#root.dataset.size = size.mode;
    this.#stage.dataset.size = size.mode;
    this.#fit();
  }

  /** Switches looks in place and repaints the last screen; the session is untouched. */
  setLook(look: Look): void {
    if (look === this.#look) return;
    this.#root.classList.remove(...this.#look.classes);
    this.#root.classList.add(...look.classes);
    this.#look = look;
    this.#syncLayers();
    this.#rowKeys.fill('');
    this.#fit(); // fonts and line height can differ between looks
    if (this.#last) this.render(this.#last);
  }

  /** What the look drew on the last screen, if it decorates. */
  get decorations(): Decorations | undefined {
    return this.#decorations;
  }

  /** What a click acts on, on the last screen: the decorations' targets, or the look's hotspots. */
  get targets(): readonly Target[] {
    return this.#targets;
  }

  /** Shows the pointer over a box (by id), or over none. */
  hover(box: string | undefined): void {
    this.#underlay?.setState('hover', box);
  }

  /** Lights the hover band of a record (by box id), or none. */
  light(band: string | undefined): void {
    this.#underlay?.setState('lit', band);
  }

  /** Shows a box as pressed while the mouse button is down on it. */
  press(box: string | undefined): void {
    this.#underlay?.setState('pressed', box);
  }

  /** Highlights a rectangle of cells over the text, or clears the highlight. */
  select(rect: Rect | undefined): void {
    if (!rect) {
      this.#selection?.remove();
      this.#selection = undefined;
      return;
    }
    if (!this.#selection) {
      this.#selection = element('div', 'selection');
      this.#selection.setAttribute('aria-hidden', 'true');
      this.#grid.append(this.#selection);
    }
    this.#selection.style.setProperty('--r', String(rect.row));
    this.#selection.style.setProperty('--c', String(rect.column));
    this.#selection.style.setProperty('--w', String(rect.columns));
    this.#selection.style.setProperty('--h', String(rect.rows));
  }

  /** Briefly marks a box as copied. */
  copied(box: string): void {
    clearTimeout(this.#copiedTimer);
    this.#underlay?.setState('copied', box);
    this.#copiedTimer = setTimeout(() => this.#underlay?.setState('copied', undefined), 1200);
  }

  render(snapshot: ScreenSnapshot): void {
    this.#last = snapshot;
    const lines = snapshot.rows + (snapshot.messageLine ? 1 : 0);
    if (snapshot.columns !== this.#columns || lines !== this.#lines) {
      this.#columns = snapshot.columns;
      this.#lines = lines;
      this.#fit();
    }
    this.#decorations = this.#look.decorate?.(snapshot);
    this.#targets = this.#decorations?.targets ?? this.#look.hotspots?.(snapshot) ?? [];
    this.#renderRows(snapshot, this.#decorations);
    this.#underlay?.paint(this.#decorations?.boxes ?? []);
    const { cursor } = snapshot;
    this.#grid.style.setProperty('--cursor-row', String(cursor.row - 1));
    this.#grid.style.setProperty('--cursor-column', String(cursor.column - 1));
    this.#cursor.classList.toggle('insert', snapshot.insertMode);
    this.#cursor.classList.toggle('waiting', snapshot.fieldExitRequired);
    // Clickable parts dim while the host has the keyboard; clicks wait for it.
    if (this.#look.decorate) this.#root.classList.toggle('locked', snapshot.keyboard === 'locked');
    this.#renderStatus(snapshot, this.#decorations?.fieldInfo);
  }

  /** Only a look that decorates has layers; Classic's DOM stays as it always was. */
  #syncLayers(): void {
    if (this.#look.decorate && !this.#underlay) {
      this.#underlay = new Layer(this.#grid, 'underlay');
      this.#keys = statusKeys();
    } else if (!this.#look.decorate && this.#underlay) {
      this.#underlay.remove();
      this.#underlay = undefined;
      this.#keys?.remove();
      this.#keys = undefined;
      this.#root.classList.remove('locked');
    }
  }

  /**
   * The screen position under a point, for clicks. With `clamp`, a point outside the
   * screen gives the nearest edge cell, so a drag can run past the edge.
   */
  positionAt(clientX: number, clientY: number, clamp = false): Position | undefined {
    const box = this.#grid.getBoundingClientRect();
    let column = Math.floor(((clientX - box.left) / box.width) * this.#columns) + 1;
    let row = Math.floor(((clientY - box.top) / box.height) * this.#lines) + 1;
    if (clamp) {
      column = Math.min(this.#columns, Math.max(1, column));
      row = Math.min(this.#lines, Math.max(1, row));
    }
    if (column < 1 || column > this.#columns || row < 1 || row > this.#lines) return undefined;
    return { row, column };
  }

  /**
   * Slides rows `first` to `last` (1-based) into place, up after Page Down or down after Page
   * Up, so a new page of a table feels like scrolling. Only how it appears; the screen is
   * already the new one.
   */
  slide(direction: 'up' | 'down', first: number, last: number): void {
    const rows = this.#rows.slice(first - 1, last);
    for (const row of rows) row.classList.remove('slide-up', 'slide-down');
    void this.#grid.offsetWidth; // restart the animation
    for (const row of rows) row.classList.add(`slide-${direction}`);
  }

  /** A short flash for the alarm; the page adds the sound. */
  flash(): void {
    this.#root.classList.remove('flash');
    void this.#root.offsetWidth; // restart the animation
    this.#root.classList.add('flash');
  }

  /**
   * Sizes the font for the chosen screen size. Scale fits the whole screen in the stage (or
   * the whole display in full screen): the full width if the stage is tall enough, otherwise
   * its height. Stretch fills the height and widens or narrows the cells to fill the width,
   * and the rows' text with them (--glyph-scale). Fixed takes the user's font size.
   */
  #fit(): void {
    this.#charRatio = measureCharRatio(this.#root);
    const style = getComputedStyle(this.#root);
    const lineHeight = parseFloat(style.getPropertyValue('--line-height')) || 1.3;
    const padX = parseFloat(style.paddingLeft) + parseFloat(style.paddingRight);
    const padY = parseFloat(style.paddingTop) + parseFloat(style.paddingBottom);
    const fullscreen = document.fullscreenElement === this.#root;
    const height = fullscreen ? this.#root.clientHeight : this.#stage.clientHeight;
    // The status line takes about one more line at 85% size, plus its rule and gap.
    const byHeight = (Math.max(200, height) - padY - 12) / ((this.#lines + 0.85) * lineHeight);
    const width = (fullscreen ? this.#root.clientWidth : this.#stage.clientWidth) - padX;
    const byWidth = width / this.#columns / this.#charRatio;
    const size = this.#size;
    const fontSize =
      size.mode === 'fixed'
        ? size.fontSize
        : Math.max(6, size.mode === 'stretch' ? byHeight : Math.min(byWidth, byHeight));
    const glyphWidth = fontSize * this.#charRatio;
    const cellWidth = size.mode === 'stretch' ? Math.max(1, width / this.#columns) : glyphWidth;
    this.#root.style.setProperty('--font-size', `${fontSize}px`);
    this.#root.style.setProperty('--cell-width', `${cellWidth}px`);
    this.#root.style.setProperty('--glyph-width', `${glyphWidth}px`);
    this.#root.style.setProperty('--glyph-scale', String(cellWidth / glyphWidth));
    this.#root.style.setProperty('--columns', String(this.#columns));
    this.#root.style.setProperty('--lines', String(this.#lines));
  }

  #renderRows(snapshot: ScreenSnapshot, decorations: Decorations | undefined): void {
    const { rows, columns } = snapshot;
    const lines = screenLines(snapshot);
    while (this.#rows.length < lines.length) {
      const row = element('div', 'row');
      this.#grid.insertBefore(row, this.#cursor);
      this.#rows.push(row);
      this.#rowKeys.push('');
    }
    while (this.#rows.length > lines.length) {
      this.#rows.pop()?.remove();
      this.#rowKeys.pop();
    }
    // ACS shows an operator error's text on the error line until the error is cleared; the
    // buffer keeps the line underneath.
    const error = snapshot.error;
    const errorText = error?.kind === 'operator' ? ` ${operatorErrorText(error.code)}` : undefined;
    const styles = decorations
      ? rowStyles(decorations.text, decorations.masks, lines.length, columns)
      : [];
    lines.forEach((line, r) => {
      const runs =
        errorText !== undefined && r === snapshot.errorRow - 1
          ? [
              {
                className: 'c-white operator-error',
                text: errorText.padEnd(columns).slice(0, columns),
              },
            ]
          : rowRuns(line.bytes, line.attributes, styles[r]);
      const key = runs.map((run) => `${run.className}\u0000${run.text}`).join('\u0001');
      if (key === this.#rowKeys[r]) return;
      this.#rowKeys[r] = key;
      this.#rows[r].classList.toggle(
        'message-line',
        snapshot.messageLine !== undefined && r === rows,
      );
      this.#rows[r].replaceChildren(
        ...runs.map((run) => {
          const span = element('span', run.className);
          span.textContent = run.text;
          return span;
        }),
      );
    });
  }

  #renderStatus(snapshot: ScreenSnapshot, fieldInfo: string | undefined): void {
    const parts: [string, string][] = [];
    if (snapshot.keyboard === 'locked') parts.push(['inhibited', 'X SYSTEM']);
    if (snapshot.error) {
      const { kind, code, help } = snapshot.error;
      const text = kind === 'operator' ? `X II ${String(code).padStart(4, '0')}` : 'X II';
      parts.push(['inhibited', help ? `${text} (Help sent)` : text]);
    }
    if (snapshot.messageWaiting) parts.push(['indicator', 'MW']);
    if (snapshot.insertMode) parts.push(['indicator', 'INS']);
    if (snapshot.fieldExitRequired) parts.push(['indicator', 'Field Exit']);
    if (snapshot.typeAhead > 0) parts.push(['indicator', `Type-ahead ${snapshot.typeAhead}`]);
    if (fieldInfo) parts.push(['field-info', fieldInfo]);
    const { row, column } = snapshot.cursor;
    const position = element('span', 'position');
    position.textContent = `${String(row).padStart(2, '0')}/${String(column).padStart(3, '0')}`;
    this.#status.replaceChildren(
      ...parts.map(([className, text]) => {
        const span = element('span', className);
        span.textContent = text;
        return span;
      }),
      ...(this.#keys ? [this.#keys] : []),
      position,
    );
  }
}

/**
 * Enter, Page Up and Page Down as buttons. They are never focused (Tab is a 5250 key);
 * the pointer handler presses their keys.
 */
function statusKeys(): HTMLElement {
  const keys = element('div', 'keys');
  for (const [key, label, shortcut] of [
    ['RollDown', 'Page Up', 'PageUp'],
    ['RollUp', 'Page Down', 'PageDown'],
    ['Enter', 'Enter', 'Enter'],
  ]) {
    const button = element('button', 'key');
    button.setAttribute('type', 'button');
    button.setAttribute('tabindex', '-1');
    button.setAttribute('aria-keyshortcuts', shortcut);
    button.dataset.key = key;
    button.textContent = label;
    keys.append(button);
  }
  return keys;
}

function element(tag: string, className: string): HTMLElement {
  const node = document.createElement(tag);
  node.className = className;
  return node;
}

/** Width of one character as a fraction of the font size, for the terminal's font. */
function measureCharRatio(root: HTMLElement): number {
  const probe = element('span', 'probe');
  probe.textContent = '0'.repeat(100);
  root.append(probe);
  const ratio = probe.getBoundingClientRect().width / 100 / 100;
  probe.remove();
  return ratio > 0 ? ratio : 0.6;
}
