// Sharing a session: the owner names people from the directory, each View-Only or Full Access,
// and gets a link. Only those people can open it, and only while the session is open. Full
// Access can take the keyboard; View-Only only watches. The dialog also lists everyone the
// session is shared with now: the owner can change one person's access, which takes effect at
// once in their browser, or remove them, which sends them away. Revoking a link removes all
// its people.

import { api, type DirectoryEntry } from './admin/api.ts';
import { button, h } from './admin/dom.ts';
import { picker } from './admin/picker.ts';
import { opened } from './lobby.ts';
import { closeOnBackdrop } from './notice.ts';
import './share.css';

type Access = 'view' | 'full';

interface Recipient {
  id: string;
  upn: string;
  name: string | null;
  access: Access;
  /** They have the session open now. */
  watching: boolean;
}

interface Share {
  id: string;
  createdAt: string;
  url: string;
  recipients: Recipient[];
}

function sharesPath(sessionId: string): string {
  return `/api/sessions/${encodeURIComponent(sessionId)}/shares`;
}

/** How many people one of the user's sessions is shared with now. */
export async function sharedCount(sessionId: string): Promise<number> {
  const { shares } = await api<{ shares: Share[] }>('GET', sharesPath(sessionId));
  return new Set(shares.flatMap((share) => share.recipients.map((r) => r.upn.toLowerCase()))).size;
}

const ACCESS_LABEL: Record<Access, string> = { view: 'View-Only', full: 'Full Access' };

/** Opens the share dialog for one of the user's sessions. */
export function openShareDialog(sessionId: string, label: string, closed?: () => void): void {
  const chosen = new Map<string, { entry: DirectoryEntry; access: Access }>();
  const people = h('ul', { class: 'share-people', 'aria-label': 'People to share with' });
  const status = h('p', { class: 'share-status', role: 'status' });
  const result = h('div', { class: 'share-result', hidden: true });
  const existing = h('div', { class: 'share-existing' });
  const create = button('Create link', () => void createLink(), 'share-btn share-btn-primary');

  function renderPeople(): void {
    people.replaceChildren(
      ...[...chosen.entries()].map(([upn, person]) => {
        const access = h(
          'select',
          { class: 'share-access', 'aria-label': `Access for ${upn}` },
          ...(['view', 'full'] as const).map((value) =>
            h('option', { value, text: ACCESS_LABEL[value], selected: person.access === value }),
          ),
        );
        access.addEventListener('change', () => (person.access = access.value as Access));
        const remove = button(
          '×',
          () => {
            chosen.delete(upn);
            renderPeople();
          },
          'share-remove',
        );
        remove.setAttribute('aria-label', `Don’t share with ${upn}`);
        return h(
          'li',
          { class: 'share-person' },
          h('span', { text: person.entry.name ? `${person.entry.name} (${upn})` : upn }),
          access,
          remove,
        );
      }),
    );
    create.disabled = chosen.size === 0;
  }

  async function createLink(): Promise<void> {
    status.textContent = '';
    try {
      const share = await api<Share>('POST', sharesPath(sessionId), {
        recipients: [...chosen.entries()].map(([upn, person]) => ({
          upn,
          access: person.access,
        })),
      });
      chosen.clear();
      renderPeople();
      showLink(share);
      await loadExisting();
    } catch (error) {
      status.textContent = (error as Error).message;
    }
  }

  function showLink(share: Share): void {
    const input = h('input', {
      class: 'share-url',
      readonly: true,
      value: share.url,
      'aria-label': 'Share link',
    });
    const copy = button(
      'Copy link',
      () => {
        void navigator.clipboard.writeText(share.url).then(
          () => (status.textContent = 'Link copied. Send it to the people you named.'),
          () => {
            input.select();
            status.textContent = 'Select the link and copy it.';
          },
        );
      },
      'share-btn',
    );
    result.replaceChildren(
      h('p', { text: 'Only the people you named can use this link.' }),
      h('div', { class: 'share-row' }, input, copy),
    );
    result.hidden = false;
    input.focus();
    input.select();
  }

  async function loadExisting(): Promise<void> {
    const { shares } = await api<{ shares: Share[] }>('GET', sharesPath(sessionId));
    existing.replaceChildren(
      ...(shares.length === 0
        ? []
        : [
            h('h3', { text: 'Shared with' }),
            h(
              'ul',
              { class: 'share-links', 'aria-label': 'Shared with' },
              ...shares.map((share) =>
                h(
                  'li',
                  { class: 'share-link', 'data-share': String(share.id) },
                  h(
                    'div',
                    { class: 'share-link-head' },
                    h('span', { text: `Link made ${opened(share.createdAt)}` }),
                    button(
                      'Copy link',
                      () => void navigator.clipboard.writeText(share.url),
                      'share-btn',
                    ),
                    button('Revoke link', () => void revoke(share), 'share-btn share-btn-danger'),
                  ),
                  h(
                    'ul',
                    { class: 'share-recipients' },
                    ...share.recipients.map((person) => recipientRow(share, person)),
                  ),
                ),
              ),
            ),
          ]),
    );
  }

  /** One person on a share: who, whether they're watching, their access, and Remove. */
  function recipientRow(share: Share, person: Recipient): HTMLElement {
    const who = person.name ? `${person.name} (${person.upn})` : person.upn;
    const path = `${sharesPath(sessionId)}/${share.id}/recipients/${person.id}`;
    const access = h(
      'select',
      { class: 'share-access', 'aria-label': `Access for ${person.upn}` },
      ...(['view', 'full'] as const).map((value) =>
        h('option', { value, text: ACCESS_LABEL[value], selected: person.access === value }),
      ),
    );
    access.addEventListener('change', () => {
      const value = access.value as Access;
      void api('PATCH', path, { access: value }).then(
        () => {
          person.access = value;
          status.textContent = `${person.upn} now has ${ACCESS_LABEL[value]}.`;
        },
        (error: unknown) => {
          access.value = person.access;
          status.textContent = (error as Error).message;
        },
      );
    });
    const remove = button(
      'Remove',
      () => {
        void api('DELETE', path).then(
          async () => {
            status.textContent = `${person.upn} can no longer see this session.`;
            await loadExisting();
          },
          (error: unknown) => (status.textContent = (error as Error).message),
        );
      },
      'share-btn share-btn-danger',
    );
    remove.setAttribute('aria-label', `Stop sharing with ${person.upn}`);
    return h(
      'li',
      { class: 'share-person', 'data-upn': person.upn },
      h('span', { text: who }),
      ...(person.watching ? [h('span', { class: 'share-watching', text: 'Watching now' })] : []),
      access,
      remove,
    );
  }

  async function revoke(share: Share): Promise<void> {
    try {
      await api('DELETE', `${sharesPath(sessionId)}/${share.id}`);
      status.textContent = 'Revoked: those people can no longer see this session.';
      result.hidden = true;
      await loadExisting();
    } catch (error) {
      status.textContent = (error as Error).message;
    }
  }

  const closeButton = button('×', () => dialog.close(), 'share-close');
  closeButton.setAttribute('aria-label', 'Close');
  closeButton.title = 'Close (Esc)';
  const dialog = h(
    'dialog',
    { class: 'share-dialog', 'aria-labelledby': 'share-title' },
    h(
      'div',
      { class: 'share-head' },
      h('h2', { id: 'share-title', text: `Share your session on ${label}` }),
      closeButton,
    ),
    h('p', {
      class: 'share-intro',
      text:
        'The people you name can open this session while it is open. View-Only watches; Full Access ' +
        'can take the keyboard and works as you, under your IBM i user.',
    }),
    picker({
      label: 'Add a person',
      placeholder: 'Name or UPN',
      search: async (q) =>
        (
          await api<{ users: DirectoryEntry[] }>(
            'GET',
            `/api/directory/users?q=${encodeURIComponent(q)}`,
          )
        ).users,
      pick: (entry) => {
        const upn = entry.upn ?? entry.name;
        if (!chosen.has(upn)) chosen.set(upn, { entry, access: 'view' });
        renderPeople();
      },
    }),
    people,
    h('div', { class: 'share-row' }, create),
    status,
    result,
    existing,
  );
  dialog.addEventListener('close', () => {
    dialog.remove();
    closed?.();
  });
  // Keys typed here belong to the dialog, not the terminal under it.
  dialog.addEventListener('keydown', (event) => event.stopPropagation());
  closeOnBackdrop(dialog);
  document.body.append(dialog);
  renderPeople();
  void loadExisting().catch((error: unknown) => (status.textContent = String(error)));
  dialog.showModal();
}
