// A notice the user must see and dismiss: a modal dialog with a title, a message and OK, for
// what would be missed in the status line, such as a session the server won't open. Enter,
// Space, Escape or a click off it closes it, and the keys go back to the screen.

import { button, h } from './admin/dom.ts';
import './notice.css';

/** A notice on the screen. */
export interface Notice {
  /** Whether it is still showing. */
  readonly open: boolean;
  /** Adds another message under the first, and gives the notice a title that covers both. */
  add(title: string, message: string): void;
}

/** Shows a notice; `closed` runs once it is dismissed. */
export function showNotice(title: string, message: string, closed?: () => void): Notice {
  const ok = button('OK', () => dialog.close(), 'notice-btn');
  const heading = h('h2', { id: 'notice-title', text: title });
  const body = h('div', { id: 'notice-message' }, h('p', { text: message }));
  const dialog = h(
    'dialog',
    {
      class: 'notice-dialog',
      role: 'alertdialog',
      'aria-labelledby': 'notice-title',
      'aria-describedby': 'notice-message',
    },
    heading,
    body,
    h('div', { class: 'notice-row' }, ok),
  );
  dialog.addEventListener('close', () => {
    dialog.remove();
    closed?.();
  });
  // Keys typed here belong to the dialog, not the terminal under it.
  dialog.addEventListener('keydown', (event) => event.stopPropagation());
  closeOnBackdrop(dialog);
  document.body.append(dialog);
  dialog.showModal();
  ok.focus();
  return {
    get open() {
      return dialog.open;
    },
    add(title, message) {
      heading.textContent = title;
      body.append(h('p', { text: message }));
    },
  };
}

/**
 * Closes a modal dialog when the user clicks off it, on the backdrop. The press must start
 * there too, so dragging out of a text box in the dialog doesn't close it.
 */
export function closeOnBackdrop(dialog: HTMLDialogElement): void {
  const outside = (event: MouseEvent) => {
    if (event.target !== dialog) return false;
    const box = dialog.getBoundingClientRect();
    return (
      event.clientX < box.left ||
      event.clientX > box.right ||
      event.clientY < box.top ||
      event.clientY > box.bottom
    );
  };
  let pressedOutside = false;
  dialog.addEventListener('pointerdown', (event) => (pressedOutside = outside(event)));
  dialog.addEventListener('click', (event) => {
    if (pressedOutside && outside(event)) dialog.close();
    pressedOutside = false;
  });
}

/**
 * Asks for a value in a dialog: a macro's prompt. A secret one (a password) has a hidden box.
 * Resolves with the value, or undefined when cancelled. Nothing is kept.
 */
export function askValue(
  title: string,
  label: string,
  value: string,
  secret: boolean,
): Promise<string | undefined> {
  return new Promise((resolve) => {
    let answer: string | undefined;
    const input = h('input', {
      class: 'notice-input',
      type: secret ? 'password' : 'text',
      value,
      autocomplete: 'off',
      'aria-label': label,
    });
    const ok = h('button', { type: 'submit', class: 'notice-btn', text: 'OK' });
    const cancel = button('Cancel', () => dialog.close(), 'notice-btn notice-btn-quiet');
    const form = h(
      'form',
      { method: 'dialog' },
      h('h2', { id: 'ask-title', text: title }),
      h('label', { class: 'notice-label' }, h('span', { text: label }), input),
      h('div', { class: 'notice-row' }, cancel, ok),
    );
    form.addEventListener('submit', (event) => {
      event.preventDefault();
      answer = input.value;
      dialog.close();
    });
    const dialog = h('dialog', { class: 'notice-dialog', 'aria-labelledby': 'ask-title' }, form);
    dialog.addEventListener('close', () => {
      dialog.remove();
      resolve(answer);
    });
    dialog.addEventListener('keydown', (event) => event.stopPropagation());
    document.body.append(dialog);
    dialog.showModal();
    input.focus();
    input.select();
  });
}
