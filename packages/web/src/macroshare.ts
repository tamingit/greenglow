// Sharing a macro: the owner names people (anyone in the directory) and groups (ones they are
// in), who may then play it from their Macros panel but never change it. The dialog lists who
// it is shared with now, each with × to take it back.

import { api, type DirectoryEntry } from './admin/api.ts';
import { button, h } from './admin/dom.ts';
import { picker } from './admin/picker.ts';
import { closeOnBackdrop } from './notice.ts';
import './share.css';

interface MacroShare {
  id: string;
  type: 'user' | 'group';
  name: string;
}

/** Opens the share dialog for one of the user's macros; `closed` runs when it closes. */
export function openMacroShareDialog(
  macro: { id: string; name: string },
  closed: () => void,
): void {
  const path = `/api/macros/${macro.id}/shares`;
  const status = h('p', { class: 'share-status', role: 'status' });
  const list = h('ul', { class: 'share-recipients', 'aria-label': 'Shared with' });

  async function load(): Promise<void> {
    const { shares } = await api<{ shares: MacroShare[] }>('GET', path);
    list.replaceChildren(
      ...(shares.length === 0
        ? [h('li', { class: 'share-person', text: 'No one yet.' })]
        : shares.map((share) => {
            const remove = button('Remove', () => void take(share), 'share-btn share-btn-danger');
            remove.setAttribute('aria-label', `Stop sharing with ${share.name}`);
            return h(
              'li',
              { class: 'share-person', 'data-share': String(share.id) },
              h('span', { text: share.type === 'group' ? `Group: ${share.name}` : share.name }),
              remove,
            );
          })),
    );
  }

  async function give(body: object, who: string): Promise<void> {
    try {
      await api('POST', path, body);
      status.textContent = `Shared with ${who}.`;
    } catch (error) {
      status.textContent = (error as Error).message;
    }
    await load();
  }

  async function take(share: MacroShare): Promise<void> {
    try {
      await api('DELETE', `${path}/${share.id}`);
      status.textContent = `${share.name} can no longer play it.`;
    } catch (error) {
      status.textContent = (error as Error).message;
    }
    await load();
  }

  const search = (url: string, key: 'users' | 'groups') => async (q: string) =>
    (await api<Record<string, DirectoryEntry[]>>('GET', `${url}?q=${encodeURIComponent(q)}`))[
      key
    ] ?? [];
  const close = button('×', () => dialog.close(), 'share-close');
  close.setAttribute('aria-label', 'Close');
  close.title = 'Close (Esc)';
  const dialog = h(
    'dialog',
    { class: 'share-dialog', 'aria-labelledby': 'macro-share-title' },
    h(
      'div',
      { class: 'share-head' },
      h('h2', { id: 'macro-share-title', text: `Share the macro ${macro.name}` }),
      close,
    ),
    h('p', {
      class: 'share-intro',
      text: 'The people and groups you name can play it from their Macros panel. Only you can change it.',
    }),
    picker({
      label: 'Add a person',
      placeholder: 'Name or UPN',
      search: search('/api/directory/users', 'users'),
      pick: (entry) =>
        void give({ type: 'user', upn: entry.upn ?? entry.name }, entry.upn ?? entry.name),
    }),
    picker({
      label: 'Add a group you are in',
      placeholder: 'Group name',
      search: search('/api/directory/my-groups', 'groups'),
      pick: (entry) =>
        void give({ type: 'group', groupId: entry.id, name: entry.name }, entry.name),
    }),
    status,
    h('h3', { text: 'Shared with' }),
    list,
  );
  dialog.addEventListener('close', () => {
    dialog.remove();
    closed();
  });
  // Keys typed here belong to the dialog, not the terminal under it.
  dialog.addEventListener('keydown', (event) => event.stopPropagation());
  closeOnBackdrop(dialog);
  document.body.append(dialog);
  void load().catch((error: unknown) => (status.textContent = String(error)));
  dialog.showModal();
}
