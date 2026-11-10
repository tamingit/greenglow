// The session tabs, in the top bar: one per 5250 session in this page, named after the screen
// it shows and coloured for its system, with a lock for how its connection is protected (green
// verified TLS, yellow TLS without a check, red plain Telnet), a close button each and a + for
// a new one. Alt+1 to Alt+0 go to the first ten tabs, and Alt+N opens a new one (main.ts). It is
// page chrome: the arrow keys move between tabs here and never reach the terminal.

/** At most this many tabs: Alt+1 to Alt+9 and Alt+0 reach every one. */
export const MAX_TABS = 10;

export interface TabView {
  id: number;
  /** The screen's title, or the system's name before the first screen. */
  title: string;
  /** What the tab's tooltip adds: the system. */
  system: string;
  /** The system's colour (#rrggbb), if it has one. */
  color: string | undefined;
  /** Connected to a session (else it shows the Disconnected card). */
  connected: boolean;
  /** How its system's connection is protected, once it has a system. */
  security: { state: 'tls' | 'unverified' | 'plain'; text: string; title: string } | undefined;
}

export interface TabActions {
  /** `focus`: move to the screen (a click or Enter), not stay on the tabs (the arrow keys). */
  select(id: number, focus: boolean): void;
  close(id: number): void;
  add(): void;
}

export class TabStrip {
  readonly #list: HTMLElement;
  readonly #add: HTMLButtonElement;
  readonly #actions: TabActions;

  /** `root` holds the strip; `actions` act on the tabs by id. */
  constructor(root: HTMLElement, actions: TabActions) {
    this.#actions = actions;
    this.#list = document.createElement('div');
    this.#list.className = 'tab-list';
    this.#list.setAttribute('role', 'tablist');
    this.#list.setAttribute('aria-label', 'Sessions');
    this.#list.addEventListener('keydown', (event) => this.#key(event));
    this.#add = document.createElement('button');
    this.#add.type = 'button';
    this.#add.className = 'tab-add';
    this.#add.setAttribute('aria-label', 'New session');
    this.#add.title = 'New session (Alt+N)';
    this.#add.append(icon('M8 3v10M3 8h10'));
    this.#add.addEventListener('click', () => actions.add());
    root.append(this.#list, this.#add);
  }

  /** Shows the tabs, with `active` selected. */
  show(tabs: readonly TabView[], active: number): void {
    const focused = this.#list.contains(document.activeElement);
    this.#list.replaceChildren(
      ...tabs.map((tab, index) => this.#tab(tab, index, tab.id === active)),
    );
    if (focused)
      this.#tabs()
        .find((tab) => tab.ariaSelected === 'true')
        ?.focus();
    this.#add.disabled = tabs.length >= MAX_TABS;
    this.#add.title =
      tabs.length >= MAX_TABS ? `At most ${MAX_TABS} sessions` : 'New session (Alt+N)';
  }

  #tab(view: TabView, index: number, selected: boolean): HTMLElement {
    const item = document.createElement('div');
    item.className = 'tab-item';
    item.dataset.tab = String(view.id);
    item.toggleAttribute('data-connected', view.connected);
    if (view.color) {
      item.dataset.colored = '';
      item.style.setProperty('--system-color', view.color);
    }
    const tab = document.createElement('button');
    tab.type = 'button';
    tab.className = 'tab';
    tab.setAttribute('role', 'tab');
    tab.setAttribute('aria-selected', String(selected));
    tab.tabIndex = selected ? 0 : -1;
    const key = index < 9 ? String(index + 1) : index === 9 ? '0' : undefined;
    tab.title =
      `${view.title} · ${view.system}${view.security ? ` · ${view.security.text}` : ''}` +
      (key ? ` (Alt+${key})` : '');
    const text = document.createElement('span');
    text.className = 'tab-title';
    text.textContent = view.title;
    tab.append(text);
    tab.addEventListener('click', () => this.#actions.select(view.id, true));
    // A middle click closes, as browsers' own tabs do.
    tab.addEventListener('auxclick', (event) => {
      if (event.button !== 1) return;
      event.preventDefault();
      this.#actions.close(view.id);
    });
    const close = document.createElement('button');
    close.type = 'button';
    close.className = 'tab-close';
    close.tabIndex = -1;
    close.setAttribute('aria-label', `Close ${view.title}`);
    close.title = 'Close (Delete)';
    close.append(icon('M4.5 4.5l7 7M11.5 4.5l-7 7'));
    close.addEventListener('click', () => this.#actions.close(view.id));
    if (view.security) item.append(lock(view.security));
    item.append(tab, close);
    return item;
  }

  #tabs(): HTMLButtonElement[] {
    return [...this.#list.querySelectorAll<HTMLButtonElement>('.tab')];
  }

  /** Left and Right (Home, End) move between tabs and select them; Delete closes one. */
  #key(event: KeyboardEvent): void {
    const tabs = this.#tabs();
    const at = tabs.indexOf(document.activeElement as HTMLButtonElement);
    if (at < 0) return;
    let to: number;
    if (event.key === 'ArrowRight') to = (at + 1) % tabs.length;
    else if (event.key === 'ArrowLeft') to = (at - 1 + tabs.length) % tabs.length;
    else if (event.key === 'Home') to = 0;
    else if (event.key === 'End') to = tabs.length - 1;
    else if (event.key === 'Delete') {
      event.preventDefault();
      this.#actions.close(idOf(tabs[at]));
      return;
    } else return;
    event.preventDefault();
    tabs[to].focus();
    this.#actions.select(idOf(tabs[to]), false);
  }
}

function idOf(tab: HTMLElement): number {
  return Number((tab.parentElement as HTMLElement).dataset.tab);
}

function icon(d: string): SVGSVGElement {
  const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
  svg.setAttribute('viewBox', '0 0 16 16');
  svg.setAttribute('aria-hidden', 'true');
  svg.setAttribute('class', 'bar-icon');
  const path = document.createElementNS('http://www.w3.org/2000/svg', 'path');
  path.setAttribute('d', d);
  svg.append(path);
  return svg;
}

/** The lock before a tab's title: closed for TLS, open for plain Telnet, and what it means in words. */
function lock(security: NonNullable<TabView['security']>): HTMLElement {
  const box = document.createElement('span');
  box.className = 'tab-lock';
  box.dataset.security = security.state;
  box.title = security.title;
  const svg = icon(
    security.state === 'plain'
      ? 'M4 7.5h8v6H4ZM5.75 7.5V5.5a2.25 2.25 0 0 1 4.4-.7'
      : 'M4 7.5h8v6H4ZM5.75 7.5V5.5a2.25 2.25 0 0 1 4.5 0v2',
  );
  box.setAttribute('role', 'img');
  box.setAttribute('aria-label', security.text);
  box.append(svg);
  return box;
}
