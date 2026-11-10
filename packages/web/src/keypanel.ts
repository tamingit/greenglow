// The keys panel, docked to the right of the screen (Alt+K, or the menu): F1 to F24 in two
// columns (F1 | F2 through F23 | F24), then Field Exit | Erase Input, Page Up | Page Down and
// Enter, and under Other keys (folded until opened, then beside them) every other key. Each
// button sends only the key it names, when pressed (the presentation-only rule), and the focus
// goes back to the screen, so typing goes on.

import { button, h } from './admin/dom.ts';
import type { KeyName } from './protocol/index.ts';
import './keypanel.css';

interface PanelKey {
  name: KeyName;
  label: string;
  /** What it is on the keyboard, for the tooltip. */
  title: string;
}

const KEYS: PanelKey[] = [
  ...Array.from({ length: 24 }, (_, i) => ({
    name: `F${i + 1}` as KeyName,
    label: `F${i + 1}`,
    title: i < 12 ? `F${i + 1}` : `F${i + 1} (Shift+F${i - 11})`,
  })),
  { name: 'FieldExit', label: 'Field Exit', title: 'Field Exit (Ctrl+Enter)' },
  {
    name: 'EraseInput',
    label: 'Erase Input',
    title: 'Erase Input: clears every input field (Alt+End)',
  },
  { name: 'RollDown', label: 'Page Up', title: 'Page Up (Roll Down)' },
  { name: 'RollUp', label: 'Page Down', title: 'Page Down (Roll Up)' },
  { name: 'Enter', label: 'Enter', title: 'Enter' },
];

/** Every other key, under Other keys. */
const OTHER_KEYS: PanelKey[] = [
  { name: 'Backtab', label: 'Previous field', title: 'Previous field (Shift+Tab)' },
  { name: 'Tab', label: 'Next field', title: 'Next field (Tab)' },
  { name: 'FieldPlus', label: 'Field +', title: 'Field + (keypad +)' },
  { name: 'FieldMinus', label: 'Field −', title: 'Field − (keypad −)' },
  { name: 'NewLine', label: 'New Line', title: 'New Line (Shift+Enter)' },
  {
    name: 'EraseEof',
    label: 'Erase EOF',
    title: 'Erase EOF: clears the field from the cursor (Ctrl+End)',
  },
  { name: 'Reset', label: 'Reset', title: 'Reset (Left Ctrl)' },
  { name: 'Dup', label: 'Dup', title: 'Dup (Shift+Insert)' },
  { name: 'Insert', label: 'Insert', title: 'Insert' },
  { name: 'Delete', label: 'Delete', title: 'Delete' },
  { name: 'Home', label: 'Home', title: 'Home' },
  { name: 'End', label: 'End', title: 'End' },
  { name: 'Help', label: 'Help', title: 'Help' },
  { name: 'Print', label: 'Print', title: 'Print' },
  { name: 'Clear', label: 'Clear', title: 'Clear' },
  { name: 'Attention', label: 'Attention', title: 'Attention (Esc)' },
  { name: 'SystemRequest', label: 'Sys Req', title: 'System Request (Shift+Esc)' },
];

export class KeyPanel {
  readonly #root: HTMLElement;

  constructor(root: HTMLElement, press: (name: KeyName) => void, closed: () => void) {
    this.#root = root;
    const close = button('×', () => closed(), 'keys-panel-close');
    close.setAttribute('aria-label', 'Close keys panel');
    close.title = 'Close (Alt+K)';
    const grid = keyGrid(KEYS, press);
    const others = h(
      'details',
      { class: 'keys-panel-other' },
      h('summary', { text: 'Other keys' }),
      keyGrid(OTHER_KEYS, press),
    );
    root.replaceChildren(
      h(
        'div',
        { class: 'keys-panel-head' },
        h('h2', { id: 'keys-panel-title', text: 'Keys' }),
        close,
      ),
      h('div', { class: 'keys-panel-body' }, grid, others),
    );
    // Open, Other keys stands beside the main keys, so the panel widens instead of scrolling;
    // the page makes room for it too (side.css).
    others.addEventListener('toggle', () => {
      root.toggleAttribute('data-wide', others.open);
      document.documentElement.toggleAttribute('data-keys-wide', others.open);
    });
    // A press keeps the focus where it was (the screen), as the bar's keys do.
    root.addEventListener('mousedown', (event) => {
      if ((event.target as Element).closest('.keys-panel-key')) event.preventDefault();
    });
  }

  get isOpen(): boolean {
    return !this.#root.hidden;
  }

  set open(open: boolean) {
    this.#root.hidden = !open;
  }
}

function keyGrid(keys: readonly PanelKey[], press: (name: KeyName) => void): HTMLElement {
  const grid = h('div', { class: 'keys-panel-grid' });
  for (const key of keys) {
    const node = button(key.label, () => press(key.name), 'keys-panel-key');
    node.title = key.title;
    node.dataset.key = key.name;
    grid.append(node);
  }
  return grid;
}
