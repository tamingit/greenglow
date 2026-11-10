// A search box over the directory: type part of a name or UPN, pick from the list. Used for
// administrators, connection access, and sharing a session.

import '../picker.css';
import type { DirectoryEntry } from './api.ts';
import { h } from './dom.ts';

export interface PickerOptions {
  label: string;
  placeholder: string;
  search(query: string): Promise<DirectoryEntry[]>;
  pick(entry: DirectoryEntry): void;
}

let pickers = 0;

export function picker(options: PickerOptions): HTMLElement {
  const id = `picker-${++pickers}`;
  const input = h('input', {
    type: 'search',
    id,
    class: 'admin-input',
    placeholder: options.placeholder,
    autocomplete: 'off',
    role: 'combobox',
    'aria-expanded': 'false',
    'aria-controls': `${id}-list`,
  });
  const list = h('ul', { id: `${id}-list`, class: 'picker-list', role: 'listbox', hidden: true });
  let timer: ReturnType<typeof setTimeout> | undefined;
  let asked = '';

  const close = () => {
    list.hidden = true;
    input.setAttribute('aria-expanded', 'false');
  };
  const choose = (entry: DirectoryEntry) => {
    options.pick(entry);
    input.value = '';
    close();
    input.focus();
  };
  input.addEventListener('input', () => {
    clearTimeout(timer);
    const query = input.value.trim();
    if (query.length < 3) {
      close();
      return;
    }
    timer = setTimeout(() => {
      asked = query;
      void options.search(query).then(
        (entries) => {
          if (asked !== query) return;
          list.replaceChildren(
            ...(entries.length === 0
              ? [h('li', { class: 'picker-empty', text: 'No matches' })]
              : entries.map((entry) => {
                  const item = h(
                    'li',
                    { role: 'option', tabindex: '-1', class: 'picker-item' },
                    h('span', { class: 'picker-name', text: entry.name || entry.upn || entry.id }),
                    entry.upn ? h('span', { class: 'picker-upn', text: entry.upn }) : null,
                  );
                  item.addEventListener('click', () => choose(entry));
                  item.addEventListener('keydown', (event) => {
                    if (event.key === 'Enter') choose(entry);
                  });
                  return item;
                })),
          );
          list.hidden = false;
          input.setAttribute('aria-expanded', 'true');
        },
        (error: unknown) => {
          // The server's reason, such as missing Microsoft Graph permissions.
          if (asked !== query) return;
          list.replaceChildren(
            h('li', {
              class: 'picker-empty picker-error',
              role: 'alert',
              text: (error as Error).message,
            }),
          );
          list.hidden = false;
          input.setAttribute('aria-expanded', 'true');
        },
      );
    }, 200);
  });
  input.addEventListener('keydown', (event) => {
    if (event.key === 'ArrowDown') {
      event.preventDefault();
      list.querySelector<HTMLElement>('[role=option]')?.focus();
    } else if (event.key === 'Escape') close();
  });
  list.addEventListener('keydown', (event) => {
    const items = [...list.querySelectorAll<HTMLElement>('[role=option]')];
    const at = items.indexOf(document.activeElement as HTMLElement);
    if (event.key === 'ArrowDown') items[Math.min(at + 1, items.length - 1)]?.focus();
    else if (event.key === 'ArrowUp') (at <= 0 ? input : items[at - 1]).focus();
    else if (event.key === 'Escape') {
      close();
      input.focus();
    } else return;
    event.preventDefault();
  });
  return h(
    'div',
    { class: 'picker' },
    h('label', { for: id, class: 'admin-label', text: options.label }),
    input,
    list,
  );
}
