// The terminal's right-click menu: Copy, Paste, Select all, and Copy table or Copy entire
// table. Everything in it also has a key (Ctrl+C, Ctrl+V, a drag to select, Alt+Shift+C and
// Alt+C), so the keyboard never needs it.

export interface MenuItem {
  label: string;
  /** The key that does the same, shown beside the label. */
  shortcut?: string;
  disabled?: boolean;
  run(): void;
}

export class ContextMenu {
  readonly #root: HTMLElement;
  /** Where the focus goes back to when the menu closes. */
  readonly #home: HTMLElement;

  constructor(home: HTMLElement) {
    this.#home = home;
    this.#root = document.createElement('div');
    this.#root.className = 'context-menu';
    this.#root.setAttribute('role', 'menu');
    this.#root.hidden = true;
    document.body.append(this.#root);
    this.#root.addEventListener('keydown', (event) => this.#key(event));
    // Pressing elsewhere closes it, as a native menu does.
    document.addEventListener(
      'pointerdown',
      (event) => {
        if (!this.#root.hidden && !this.#root.contains(event.target as Node)) this.close();
      },
      { capture: true },
    );
    window.addEventListener('blur', () => this.close());
    window.addEventListener('resize', () => this.close());
  }

  get open(): boolean {
    return !this.#root.hidden;
  }

  /** Shows the items at a point on the page, kept inside the window. */
  show(items: readonly MenuItem[], x: number, y: number): void {
    this.#root.replaceChildren(
      ...items.map((item) => {
        const button = document.createElement('button');
        button.type = 'button';
        button.className = 'context-menu-item';
        button.setAttribute('role', 'menuitem');
        button.disabled = item.disabled === true;
        const label = document.createElement('span');
        label.textContent = item.label;
        button.append(label);
        if (item.shortcut) {
          const shortcut = document.createElement('kbd');
          shortcut.textContent = item.shortcut;
          button.append(shortcut);
        }
        button.addEventListener('click', () => {
          this.close();
          item.run();
        });
        return button;
      }),
    );
    this.#root.hidden = false;
    const { width, height } = this.#root.getBoundingClientRect();
    this.#root.style.setProperty(
      '--menu-x',
      `${Math.max(4, Math.min(x, innerWidth - width - 4))}px`,
    );
    this.#root.style.setProperty(
      '--menu-y',
      `${Math.max(4, Math.min(y, innerHeight - height - 4))}px`,
    );
    this.#buttons()
      .find((b) => !b.disabled)
      ?.focus();
  }

  close(): void {
    if (this.#root.hidden) return;
    const hadFocus = this.#root.contains(document.activeElement);
    this.#root.hidden = true;
    if (hadFocus) this.#home.focus();
  }

  #buttons(): HTMLButtonElement[] {
    return [...this.#root.querySelectorAll<HTMLButtonElement>('button')];
  }

  #key(event: KeyboardEvent): void {
    // Keys here belong to the menu, never to the screen under it.
    event.stopPropagation();
    const enabled = this.#buttons().filter((b) => !b.disabled);
    const at = enabled.indexOf(document.activeElement as HTMLButtonElement);
    if (event.key === 'Escape' || event.key === 'Tab') {
      event.preventDefault();
      this.close();
    } else if (event.key === 'ArrowDown') {
      event.preventDefault();
      enabled[(at + 1) % enabled.length]?.focus();
    } else if (event.key === 'ArrowUp') {
      event.preventDefault();
      enabled[(at - 1 + enabled.length) % enabled.length]?.focus();
    }
  }
}
