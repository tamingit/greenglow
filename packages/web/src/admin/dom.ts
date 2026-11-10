// Small DOM builders for the admin page: elements are made, never parsed from HTML (the CSP
// and ESLint forbid HTML strings).

type Child = Node | string | null | undefined | false;

export function h<K extends keyof HTMLElementTagNameMap>(
  tag: K,
  props: Partial<Record<string, string | boolean>> = {},
  ...children: Child[]
): HTMLElementTagNameMap[K] {
  const node = document.createElement(tag);
  for (const [name, value] of Object.entries(props)) {
    if (value === undefined || value === false) continue;
    if (name === 'class') node.className = String(value);
    else if (name === 'text') node.textContent = String(value);
    else node.setAttribute(name, value === true ? '' : value);
  }
  for (const child of children) if (child) node.append(child);
  return node;
}

export function button(
  label: string,
  click: () => void,
  className = 'admin-btn',
): HTMLButtonElement {
  const node = h('button', { type: 'button', class: className, text: label });
  node.addEventListener('click', click);
  return node;
}

/** A date and time in the reader's locale, or a dash. */
export function when(value: string | null | undefined): string {
  if (!value) return '—';
  return new Date(value).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' });
}

/** Shows a message under the tabs: an error, or a confirmation that fades. */
export function notify(text: string, error = false): void {
  const message = document.querySelector<HTMLElement>('#admin-message');
  if (!message) return;
  message.textContent = text;
  message.dataset.error = String(error);
  message.hidden = false;
  if (!error) setTimeout(() => (message.hidden = message.textContent !== text), 4000);
}
