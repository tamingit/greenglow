// Connections: the IBM i systems users may connect to, each with its own per-user session
// limit and the Entra ID users and groups allowed to use it. A connection with no access
// listed is open to administrators only.

import { api, type DirectoryEntry } from './api.ts';
import { certificateSection, type Trust } from './certificate.ts';
import { button, h, notify } from './dom.ts';
import { picker } from './picker.ts';

interface Grant {
  id: string;
  type: 'user' | 'group';
  principalId: string;
  name: string;
}

interface Connection {
  id: string;
  name: string;
  label: string;
  url: string;
  secure: boolean;
  maxSessionsPerUser: number;
  isTest: boolean;
  enabled: boolean;
  sortOrder: number;
  /** #rrggbb, or none. */
  color: string | null;
  /** tls://: whether its certificate is checked, by the server's CA or its own. */
  verified: boolean;
  /** The CA trusted for this connection alone, if any. */
  trust: Trust | null;
  liveSessions: number;
  grants: Grant[];
}

/** The editable fields, read from a card's inputs. */
interface Fields {
  label: string;
  url: string;
  maxSessionsPerUser: number;
  isTest: boolean;
  enabled: boolean;
  /** #rrggbb, or empty for none. */
  color: string;
}

/** What the colour picker starts on before a connection has a colour. */
const NEW_COLOR = '#2f7d4f';

function fields(values: Partial<Connection>, prefix: string) {
  const label = h('input', {
    class: 'admin-input',
    id: `${prefix}-label`,
    value: values.label ?? '',
    maxlength: '40',
  });
  const url = h('input', {
    class: 'admin-input',
    id: `${prefix}-url`,
    value: values.url ?? '',
    placeholder: 'tls://host:992 or tcp://host:23',
    spellcheck: 'false',
  });
  const max = h('input', {
    class: 'admin-input',
    id: `${prefix}-max`,
    type: 'number',
    min: '1',
    max: '9',
    value: String(values.maxSessionsPerUser ?? 3),
  });
  const isTest = h('input', {
    type: 'checkbox',
    id: `${prefix}-test`,
    checked: values.isTest ?? false,
  });
  const enabled = h('input', {
    type: 'checkbox',
    id: `${prefix}-enabled`,
    checked: values.enabled ?? true,
  });
  const hasColor = h('input', {
    type: 'checkbox',
    id: `${prefix}-has-color`,
    checked: Boolean(values.color),
  });
  const color = h('input', {
    type: 'color',
    class: 'admin-color',
    id: `${prefix}-color`,
    value: values.color ?? NEW_COLOR,
    'aria-label': 'Choose the colour',
    disabled: !values.color,
  });
  hasColor.addEventListener('change', () => (color.disabled = !hasColor.checked));
  const row = h(
    'div',
    { class: 'admin-row' },
    h(
      'div',
      { class: 'admin-field' },
      h('label', { for: label.id, class: 'admin-label', text: 'Label' }),
      label,
    ),
    h(
      'div',
      { class: 'admin-field' },
      h('label', { for: url.id, class: 'admin-label', text: 'Address' }),
      url,
    ),
    h(
      'div',
      { class: 'admin-field' },
      h('label', { for: max.id, class: 'admin-label', text: 'Sessions per user' }),
      max,
    ),
    h('label', { class: 'admin-check' }, enabled, 'Enabled'),
    h(
      'label',
      {
        class: 'admin-check',
        title: 'Automated sign-on tests may run here. Never for production.',
      },
      isTest,
      'Test system',
    ),
    h(
      'span',
      {
        class: 'admin-check',
        title: 'Shown with the system on the terminal page. Users may choose their own.',
      },
      h('label', { class: 'admin-check' }, hasColor, 'Colour'),
      color,
    ),
  );
  return {
    row,
    read: (): Fields => ({
      label: label.value.trim(),
      url: url.value.trim(),
      maxSessionsPerUser: Number(max.value),
      isTest: isTest.checked,
      enabled: enabled.checked,
      color: hasColor.checked ? color.value : '',
    }),
  };
}

/** The connection's colour beside its name. Set as a custom property: the CSP refuses style attributes. */
function swatch(color: string): HTMLElement {
  const node = h('span', { class: 'admin-swatch', 'aria-hidden': 'true' });
  node.style.setProperty('--swatch', color);
  return node;
}

export function connectionsPanel(): { element: HTMLElement; load(): Promise<void> } {
  const list = h('div', { class: 'admin-panel' });
  /** Whether the server has a CA of its own, which checks every tls:// certificate. */
  let serverCa = true;

  async function load(): Promise<void> {
    const { connections, certificatesVerified } = await api<{
      connections: Connection[];
      certificatesVerified: boolean;
    }>('GET', '/api/admin/connections');
    serverCa = certificatesVerified;
    list.replaceChildren(...connections.map(card), addCard());
  }

  async function run(action: () => Promise<unknown>, done: string): Promise<void> {
    try {
      await action();
      notify(done);
      await load();
    } catch (error) {
      notify((error as Error).message, true);
    }
  }

  function card(connection: Connection): HTMLElement {
    const form = fields(connection, `c${connection.id}`);
    const chips = h(
      'ul',
      { class: 'admin-chips', 'aria-label': `Who may use ${connection.label}` },
      ...connection.grants.map((grant) =>
        h(
          'li',
          { class: 'admin-chip' },
          h('span', { text: `${grant.type === 'group' ? 'Group' : 'User'}: ${grant.name}` }),
          button(
            '×',
            () =>
              void run(
                () => api('DELETE', `/api/admin/connections/${connection.id}/grants/${grant.id}`),
                `${grant.name} can no longer use ${connection.label}.`,
              ),
            'admin-chip-remove',
          ),
        ),
      ),
    );
    for (const remove of chips.querySelectorAll('button'))
      remove.setAttribute('aria-label', 'Remove access');
    const grant = (type: 'user' | 'group') => (entry: DirectoryEntry) =>
      void run(
        () =>
          api('POST', `/api/admin/connections/${connection.id}/grants`, {
            type,
            id: entry.id,
            name: entry.upn ?? entry.name,
          }),
        `${entry.upn ?? entry.name} may now use ${connection.label}.`,
      );
    const tags = [
      connection.secure
        ? null
        : h('span', { class: 'admin-tag', 'data-tone': 'warn', text: 'not encrypted' }),
      connection.secure && !connection.verified
        ? h('span', {
            class: 'admin-tag',
            'data-tone': 'warn',
            text: 'certificate not verified',
            title:
              'No CA is trusted for it (Relay__CaPem, Relay__CaFile, or one trusted below), so it accepts any certificate.',
          })
        : null,
      connection.isTest ? h('span', { class: 'admin-tag', text: 'test system' }) : null,
      connection.enabled ? null : h('span', { class: 'admin-tag', text: 'disabled' }),
      connection.liveSessions > 0
        ? h('span', {
            class: 'admin-tag',
            'data-tone': 'live',
            text: `${connection.liveSessions} open session${connection.liveSessions === 1 ? '' : 's'}`,
          })
        : null,
    ];
    return h(
      'section',
      { class: 'admin-card', 'aria-label': connection.label, 'data-connection': connection.name },
      h(
        'h2',
        {},
        connection.color ? swatch(connection.color) : null,
        connection.label,
        h('span', { class: 'admin-tag', text: connection.name }),
        ...tags,
      ),
      form.row,
      h(
        'div',
        { class: 'admin-row' },
        button(
          'Save',
          () =>
            void run(
              () => api('PUT', `/api/admin/connections/${connection.id}`, form.read()),
              `Saved ${connection.label}.`,
            ),
        ),
        button(
          'Delete',
          () => {
            if (
              !confirm(`Delete ${connection.label}? Open sessions carry on; no new ones can start.`)
            )
              return;
            void run(
              () => api('DELETE', `/api/admin/connections/${connection.id}`),
              `Deleted ${connection.label}.`,
            );
          },
          'admin-btn admin-btn-danger',
        ),
      ),
      connection.secure ? certificateSection(connection, serverCa, load) : null,
      h('h3', { text: 'Who may use it' }),
      connection.grants.length === 0
        ? h('p', { class: 'admin-intro', text: 'No one yet: only administrators can use it.' })
        : chips,
      h(
        'div',
        { class: 'admin-row' },
        picker({
          label: 'Add a group',
          placeholder: 'Group name',
          search: async (q) =>
            (
              await api<{ groups: DirectoryEntry[] }>(
                'GET',
                `/api/admin/directory/groups?q=${encodeURIComponent(q)}`,
              )
            ).groups,
          pick: grant('group'),
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
          pick: grant('user'),
        }),
      ),
    );
  }

  function addCard(): HTMLElement {
    const name = h('input', {
      class: 'admin-input',
      id: 'new-name',
      placeholder: 'test',
      pattern: '[a-z][a-z0-9-]{0,31}',
      spellcheck: 'false',
    });
    const form = fields({}, 'new');
    return h(
      'section',
      { class: 'admin-card', 'aria-label': 'Add a connection' },
      h('h2', { text: 'Add a connection' }),
      h(
        'div',
        { class: 'admin-row' },
        h(
          'div',
          { class: 'admin-field' },
          h('label', {
            for: name.id,
            class: 'admin-label',
            text: 'Name (lowercase, can’t change later)',
          }),
          name,
        ),
      ),
      form.row,
      h(
        'div',
        { class: 'admin-row' },
        button('Add connection', () => {
          const values = form.read();
          void run(
            () => api('POST', '/api/admin/connections', { name: name.value.trim(), ...values }),
            `Added ${values.label || name.value.trim()}.`,
          );
        }),
      ),
    );
  }

  const element = h(
    'section',
    { class: 'admin-panel' },
    h('p', {
      class: 'admin-intro',
      text:
        'The IBM i systems people can open sessions on. Give access to Entra ID groups or people; ' +
        'a connection nobody has access to is open to administrators only.',
    }),
    list,
  );
  return { element, load };
}

interface Setting {
  key: string;
  value: number;
  isDefault: boolean;
  min: number;
  max: number;
}

const SETTING_TEXT: Record<string, [string, string]> = {
  'Sessions.MaxFrontends': [
    'Browsers per session',
    'How many of its owner’s own browsers may show one session at once. People it is shared with don’t count.',
  ],
  'Sessions.IdleMinutes': [
    'Minutes kept with no browser',
    'How long a session stays open after its last browser leaves, so its user can come back to it. 0 ends it at once.',
  ],
  'Sessions.MaxConnections': [
    'Sessions on the server',
    'The most sessions open at once, across everyone.',
  ],
};

export function settingsPanel(): { element: HTMLElement; load(): Promise<void> } {
  const list = h('div', { class: 'admin-panel' });

  async function load(): Promise<void> {
    const { settings } = await api<{ settings: Setting[] }>('GET', '/api/admin/settings');
    list.replaceChildren(
      ...settings.map((setting) => {
        const [title, help] = SETTING_TEXT[setting.key] ?? [setting.key, ''];
        const id = `setting-${setting.key.replace(/\W/g, '-')}`;
        const input = h('input', {
          class: 'admin-input',
          id,
          type: 'number',
          min: String(setting.min),
          max: String(setting.max),
          value: String(setting.value),
        });
        const save = async (value: number | null, done: string) => {
          try {
            await api('PUT', `/api/admin/settings/${encodeURIComponent(setting.key)}`, { value });
            notify(done);
            await load();
          } catch (error) {
            notify((error as Error).message, true);
          }
        };
        return h(
          'section',
          { class: 'admin-card' },
          h(
            'h2',
            {},
            title,
            setting.isDefault ? h('span', { class: 'admin-tag', text: 'default' }) : null,
          ),
          h('p', { class: 'admin-intro', text: help }),
          h(
            'div',
            { class: 'admin-row' },
            h(
              'div',
              { class: 'admin-field' },
              h('label', { for: id, class: 'admin-label', text: 'Value' }),
              input,
            ),
            button('Save', () => void save(Number(input.value), `Saved ${title.toLowerCase()}.`)),
            setting.isDefault
              ? null
              : button(
                  'Use the default',
                  () => void save(null, `${title} is back to the default.`),
                  'admin-btn admin-btn-quiet',
                ),
          ),
        );
      }),
    );
  }

  return {
    element: h(
      'section',
      { class: 'admin-panel' },
      h('p', {
        class: 'admin-intro',
        text: 'Session limits. The defaults come from the server’s configuration; a value saved here wins.',
      }),
      list,
    ),
    load,
  };
}
