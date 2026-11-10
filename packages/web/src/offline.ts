// The overlay on the terminal while no session shows a screen: Disconnected, with a way to
// connect, or Connecting while a new session waits for its first screen. A user who may use
// more than one system chooses one here from a tile per system; with one, a button connects
// to it. It also asks before Disconnect drops a signed-on session. The screen stays under it, blurred; before
// there has been one, a faint sign-on silhouette stands in. It is page chrome, not host
// data, and its events stop here, so the terminal's key and mouse handling never sees them.

/** A system the user may connect to, as a tile on the Disconnected card. */
export interface OfflineChoice {
  name: string;
  label: string;
  /** The security badge: verified TLS, unverified TLS, or plain Telnet. */
  security: 'tls' | 'unverified' | 'plain';
  securityText: string;
  securityTitle: string;
  /** The system's colour (#rrggbb), if it has one. */
  color?: string;
}

export type OfflineView =
  | {
      state: 'disconnected';
      detail: string;
      /** The session ended with an error rather than by a sign-off or Disconnect. */
      error: boolean;
      action: string;
      /** No target to connect to. */
      disabled: boolean;
      /** The systems to choose from; with fewer than two, the action button connects. */
      choices: readonly OfflineChoice[];
      /** The system chosen last, which the tiles mark and focus. */
      chosen: string;
    }
  | { state: 'connecting'; detail: string }
  | { state: 'confirm'; title: string; detail: string };

export interface OfflineActions {
  connect(): void;
  /** Connects to one of the choices. */
  choose(name: string): void;
  /** Confirmed: leave the session, which keeps running on the server for a while. */
  disconnect(): void;
  /** Confirmed: end the session on the server too. */
  end(): void;
  /** Not confirmed: back to the session. */
  stay(): void;
}

/**
 * A generic 5250 sign-on layout as [row, column, length, kind] on an 80×24 grid: the
 * title, the system lines, five prompts with their fields, and the copyright line.
 */
const GHOST: readonly [number, number, number, 'text' | 'field'][] = [
  [1, 36, 8, 'text'],
  [2, 48, 22, 'text'],
  [3, 48, 24, 'text'],
  [4, 48, 20, 'text'],
  ...[6, 7, 8, 9, 10].flatMap((row, i): [number, number, number, 'text' | 'field'][] => [
    [row, 17, [26, 28, 30, 27, 32][i], 'text'],
    [row, 53, 10, 'field'],
  ]),
  [24, 26, 30, 'text'],
];

const SVG = 'http://www.w3.org/2000/svg';

export class Offline {
  readonly #root: HTMLElement;
  readonly #title: HTMLElement;
  readonly #detail: HTMLElement;
  readonly #action: HTMLButtonElement;
  readonly #stay: HTMLButtonElement;
  readonly #end: HTMLButtonElement;
  readonly #lobby: HTMLElement;
  readonly #choices: HTMLElement;
  /** The choices the tiles show now, to rebuild them only when they change. */
  #shown = '';
  readonly #actions: OfflineActions;

  /** `terminal` hosts the overlay. */
  constructor(terminal: HTMLElement, actions: OfflineActions) {
    this.#actions = actions;
    this.#root = element('div', 'offline');
    this.#root.hidden = true;
    const card = element('div', 'offline-card');
    this.#title = element('p', 'offline-title');
    this.#title.id = 'offline-title';
    this.#detail = element('p', 'offline-detail');
    this.#detail.id = 'offline-detail';
    const buttons = element('div', 'offline-actions');
    this.#stay = button('offline-stay', 'Stay connected', () => actions.stay());
    this.#action = button('offline-action', '', () => {
      if (this.#root.dataset.state === 'confirm') actions.disconnect();
      else actions.connect();
    });
    this.#end = button('offline-end', 'End session', () => actions.end());
    buttons.append(this.#stay, this.#end, this.#action);
    this.#choices = element('div', 'offline-choices');
    this.#choices.setAttribute('role', 'group');
    this.#choices.setAttribute('aria-label', 'Systems');
    this.#choices.hidden = true;
    this.#lobby = element('div', 'offline-lobby');
    this.#lobby.hidden = true;
    card.append(icon(), this.#title, this.#detail, this.#choices, this.#lobby, buttons);
    this.#root.append(ghost(), card);
    for (const type of ['keydown', 'keyup', 'mousedown', 'mousemove', 'wheel'])
      this.#root.addEventListener(type, (event) => event.stopPropagation());
    this.#root.addEventListener('keydown', (event) => this.#key(event));
    terminal.append(this.#root);
  }

  show(view: OfflineView): void {
    const restart = this.#root.hidden || this.#root.dataset.state !== view.state;
    const confirm = view.state === 'confirm';
    const choosing = view.state === 'disconnected' && view.choices.length > 1;
    const newChoices = choosing && this.#setChoices(view.choices);
    this.#root.hidden = false;
    this.#root.dataset.state = view.state;
    this.#root.dataset.error = String(view.state === 'disconnected' && view.error);
    this.#title.textContent =
      view.state === 'confirm'
        ? view.title
        : view.state === 'connecting'
          ? 'Connecting…'
          : choosing
            ? 'Choose a system'
            : 'Disconnected';
    this.#detail.textContent = view.detail;
    // Asking, it is a dialog; otherwise the bar's status already speaks for it.
    if (confirm) {
      this.#root.setAttribute('role', 'alertdialog');
      this.#root.setAttribute('aria-labelledby', this.#title.id);
      this.#root.setAttribute('aria-describedby', this.#detail.id);
    } else {
      for (const name of ['role', 'aria-labelledby', 'aria-describedby'])
        this.#root.removeAttribute(name);
    }
    this.#stay.hidden = !confirm;
    this.#end.hidden = !confirm;
    this.#action.hidden = view.state === 'connecting' || choosing;
    this.#choices.hidden = !choosing;
    this.#root.classList.toggle('with-choices', choosing);
    if (choosing)
      for (const tile of this.#tiles())
        tile.classList.toggle('chosen', tile.dataset.target === view.chosen);
    if (view.state === 'confirm') {
      this.#action.textContent = 'Leave it running';
      this.#action.disabled = false;
    } else if (view.state === 'disconnected') {
      this.#action.textContent = view.action;
      this.#action.disabled = view.disabled;
    }
    this.#syncLobby();
    // Replay the entrance (and the unplug) each time the overlay comes up or changes state.
    if (restart) {
      this.#root.classList.remove('enter');
      void this.#root.offsetWidth;
      this.#root.classList.add('enter');
    }
    // The safe answer has the focus, so a stray Enter keeps the session.
    if (confirm && restart) this.#stay.focus();
    // Choosing, the last system chosen has the focus, so Enter connects to it again.
    if (choosing && (restart || newChoices))
      (this.#tiles().find((tile) => tile.classList.contains('chosen')) ?? this.#tiles()[0]).focus();
  }

  /** Builds a tile per choice, if they changed; true if they did. */
  #setChoices(choices: readonly OfflineChoice[]): boolean {
    const key = JSON.stringify(choices);
    if (key === this.#shown) return false;
    this.#shown = key;
    this.#choices.replaceChildren(
      ...choices.map((choice) => {
        const tile = button('offline-choice', '', () => this.#actions.choose(choice.name));
        tile.dataset.target = choice.name;
        if (choice.color) {
          tile.dataset.colored = '';
          tile.style.setProperty('--system-color', choice.color);
        }
        const label = element('span', 'offline-choice-label');
        label.textContent = choice.label;
        const badge = element('span', 'offline-choice-security');
        badge.dataset.security = choice.security;
        badge.textContent = choice.securityText;
        badge.title = choice.securityTitle;
        tile.append(label, badge);
        return tile;
      }),
    );
    return true;
  }

  #tiles(): HTMLButtonElement[] {
    return [...this.#choices.querySelectorAll<HTMLButtonElement>('.offline-choice')];
  }

  /** The sessions to go back to, shown while disconnected; null for none. */
  set lobby(table: HTMLElement | null) {
    if (table) this.#lobby.replaceChildren(table);
    else this.#lobby.replaceChildren();
    this.#syncLobby();
  }

  #syncLobby(): void {
    const showing =
      this.#root.dataset.state === 'disconnected' && this.#lobby.childElementCount > 0;
    this.#lobby.hidden = !showing;
    this.#root.classList.toggle('with-lobby', showing);
  }

  hide(): void {
    this.#root.hidden = true;
  }

  /** Puts the focus back on the question, if it is showing. */
  focus(): void {
    if (this.#root.dataset.state === 'confirm' && !this.#root.hidden) this.#stay.focus();
  }

  /** Whether a screen has been shown in this page; until then the silhouette stands in. */
  set blank(blank: boolean) {
    this.#root.classList.toggle('blank', blank);
  }

  /**
   * Choosing: the arrow keys, Home and End move between the tiles. Asking: Esc means stay,
   * and Tab moves between the answers only.
   */
  #key(event: KeyboardEvent): void {
    if (!this.#choices.hidden) this.#move(event);
    if (this.#root.dataset.state !== 'confirm') return;
    if (event.key === 'Escape') {
      event.preventDefault();
      this.#actions.stay();
    } else if (event.key === 'Tab') {
      event.preventDefault();
      const order = [this.#stay, this.#end, this.#action];
      const at = order.indexOf(document.activeElement as HTMLButtonElement);
      const step = event.shiftKey ? order.length - 1 : 1;
      order[(at + step) % order.length].focus();
    }
  }

  #move(event: KeyboardEvent): void {
    const tiles = this.#tiles();
    const at = tiles.indexOf(document.activeElement as HTMLButtonElement);
    if (at < 0) return;
    let to: number;
    if (event.key === 'Home') to = 0;
    else if (event.key === 'End') to = tiles.length - 1;
    else if (event.key in STEPS) to = (at + STEPS[event.key] + tiles.length) % tiles.length;
    else return;
    event.preventDefault();
    tiles[to].focus();
  }
}

const STEPS: Readonly<Record<string, number>> = {
  ArrowDown: 1,
  ArrowRight: 1,
  ArrowUp: -1,
  ArrowLeft: -1,
};

function button(className: string, label: string, click: () => void): HTMLButtonElement {
  const node = element('button', className) as HTMLButtonElement;
  node.type = 'button';
  node.textContent = label;
  node.addEventListener('click', click);
  return node;
}

/**
 * A plug and socket: plugged in while asking, pulled apart while disconnected, and pushed
 * together again and again while connecting.
 */
function icon(): HTMLElement {
  const badge = element('div', 'offline-icon');
  const svg = document.createElementNS(SVG, 'svg');
  svg.setAttribute('viewBox', '0 0 24 24');
  svg.setAttribute('aria-hidden', 'true');
  for (const [className, d] of [
    [
      'plug',
      'M1.5 12H5 M7 7.5h2a2 2 0 0 1 2 2v5a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2v-5a2 2 0 0 1 2-2Z M11 10h2.5 M11 14h2.5',
    ],
    ['socket', 'M17.5 7.5a2 2 0 0 1 2 2v5a2 2 0 0 1-2 2h-2v-9Z M19.5 12h3'],
    ['spark', 'M14.5 3.2v2.3 M10.6 4.3l1.3 1.5 M18.4 4.3l-1.3 1.5'],
  ]) {
    const path = document.createElementNS(SVG, 'path');
    path.setAttribute('class', className);
    path.setAttribute('d', d);
    svg.append(path);
  }
  badge.append(svg);
  return badge;
}

function ghost(): HTMLElement {
  const layer = element('div', 'offline-ghost');
  layer.setAttribute('aria-hidden', 'true');
  for (const [row, column, length, kind] of GHOST) {
    const bar = element('span', kind);
    bar.style.setProperty('--r', String(row - 1));
    bar.style.setProperty('--c', String(column - 1));
    bar.style.setProperty('--w', String(length));
    layer.append(bar);
  }
  return layer;
}

function element(tag: string, className: string): HTMLElement {
  const node = document.createElement(tag);
  node.className = className;
  return node;
}
