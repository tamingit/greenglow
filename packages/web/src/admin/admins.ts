// Administrators: who may manage connections and read the connection log. The first one comes
// from the Entra:InitialAdminUpn setting; administrators add more here. The last one can't be
// removed.

import { api, type DirectoryEntry } from './api.ts';
import { button, h, notify, when } from './dom.ts';
import { picker } from './picker.ts';

interface Admin {
  id: string;
  upn: string;
  name: string;
  lastSeenAt: string | null;
  initial: boolean;
}

export function adminsPanel(me: { id: string }): { element: HTMLElement; load(): Promise<void> } {
  const body = h('tbody');
  const table = h(
    'table',
    { class: 'admin-table' },
    h(
      'thead',
      {},
      h(
        'tr',
        {},
        h('th', { text: 'Name' }),
        h('th', { text: 'UPN' }),
        h('th', { text: 'Last signed in' }),
        h('th'),
      ),
    ),
    body,
  );

  async function load(): Promise<void> {
    const { admins } = await api<{ admins: Admin[] }>('GET', '/api/admin/admins');
    body.replaceChildren(
      ...admins.map((admin) =>
        h(
          'tr',
          {},
          h(
            'td',
            {},
            admin.name || '—',
            admin.initial ? h('span', { class: 'admin-tag', text: 'initial' }) : null,
          ),
          h('td', { text: admin.upn }),
          h('td', { text: admin.lastSeenAt ? when(admin.lastSeenAt) : 'Not yet' }),
          h(
            'td',
            { class: 'admin-actions' },
            button(
              admin.id === me.id ? 'Remove me' : 'Remove',
              () => void remove(admin),
              'admin-btn admin-btn-quiet',
            ),
          ),
        ),
      ),
    );
  }

  async function add(entry: DirectoryEntry): Promise<void> {
    try {
      await api('POST', '/api/admin/admins', { upn: entry.upn ?? entry.name });
      notify(`${entry.upn ?? entry.name} is now an administrator.`);
      await load();
    } catch (error) {
      notify(String((error as Error).message), true);
    }
  }

  async function remove(admin: Admin): Promise<void> {
    const self = admin.id === me.id;
    if (
      !confirm(
        self
          ? 'Stop being an administrator? You lose access to this page.'
          : `Remove ${admin.upn} as an administrator?`,
      )
    )
      return;
    try {
      await api('DELETE', `/api/admin/admins/${encodeURIComponent(admin.id)}`);
      if (self) {
        location.assign('/');
        return;
      }
      notify(`${admin.upn} is no longer an administrator.`);
      await load();
    } catch (error) {
      notify(String((error as Error).message), true);
    }
  }

  const element = h(
    'section',
    { class: 'admin-panel' },
    h('p', {
      class: 'admin-intro',
      text: 'Administrators manage connections and who may use them, the session limits, and read the connection log.',
    }),
    picker({
      label: 'Add an administrator',
      placeholder: 'Name or UPN',
      search: async (q) =>
        (
          await api<{ users: DirectoryEntry[] }>(
            'GET',
            `/api/directory/users?q=${encodeURIComponent(q)}`,
          )
        ).users,
      pick: (entry) => void add(entry),
    }),
    table,
  );
  return { element, load };
}
