// Nodes: every GreenGlow server of the cluster, with its state, version and
// sessions. Drain stops a node opening sessions before it is restarted; its sessions carry on
// until they end. Resume undoes it. Resync from scratch rebuilds a node's data from the others.

import { ApiError, api } from './api.ts';
import { button, h, notify, when } from './dom.ts';

interface NodeSession {
  id: string;
  owner: string;
  ownerName: string;
  connection: string;
  label: string;
  device: string | null;
  ibmUser: string | null;
  title: string;
  createdAt: string;
  frontends: number;
  idleSince: string | null;
  signedOn: boolean;
}

interface Node {
  id: number;
  self: boolean;
  state: 'active' | 'draining' | 'unreachable';
  error: string | null;
  version: string | null;
  protocol: number | null;
  oldestProtocol: number | null;
  startedAt: string | null;
  sessions: NodeSession[];
  /** For another node: when this one last synced with it, and the last error. */
  sync: { lastSync: string | null; lastError: string | null; lastErrorAt: string | null } | null;
}

const STATE_TEXT: Record<Node['state'], string> = {
  active: 'active',
  draining: 'draining',
  unreachable: 'unreachable',
};

function sessionsTable(sessions: NodeSession[]): HTMLElement {
  if (sessions.length === 0) return h('p', { class: 'admin-intro', text: 'No sessions open.' });
  return h(
    'div',
    { class: 'admin-table-wrap' },
    h(
      'table',
      { class: 'admin-table' },
      h(
        'thead',
        {},
        h(
          'tr',
          {},
          ...[
            'User',
            'System',
            'Device',
            'IBM i user',
            'Screen',
            'Opened',
            'Browsers',
            'Idle since',
          ].map((c) => h('th', { scope: 'col', text: c })),
        ),
      ),
      h(
        'tbody',
        {},
        ...sessions.map((s) =>
          h(
            'tr',
            {},
            h('td', { text: s.owner, title: s.ownerName || false }),
            h('td', { text: s.label }),
            h('td', { text: s.device ?? '—' }),
            h('td', { text: s.ibmUser ?? '' }),
            h('td', { text: s.title }),
            h('td', { text: when(s.createdAt) }),
            h('td', { class: 'admin-numeric', text: String(s.frontends) }),
            h('td', { text: s.idleSince ? when(s.idleSince) : '' }),
          ),
        ),
      ),
    ),
  );
}

export function nodesPanel(): { element: HTMLElement; load(): Promise<void> } {
  const list = h('div', { class: 'admin-cards' });

  async function act(path: string, body: unknown, done: string): Promise<void> {
    try {
      const answer = await api<{ message?: string } | undefined>('POST', path, body);
      notify(answer?.message ?? done);
    } catch (error) {
      notify(error instanceof ApiError ? error.message : String(error), true);
    }
    await load();
  }

  function card(node: Node, clustered: boolean): HTMLElement {
    const name = `Node ${node.id}`;
    const tone = node.state === 'active' ? 'live' : 'warn';
    const facts: string[] = [];
    if (node.version) facts.push(`Version ${node.version}`);
    if (node.protocol !== null) facts.push(`peer protocol ${node.oldestProtocol}–${node.protocol}`);
    if (node.startedAt) facts.push(`started ${when(node.startedAt)}`);
    if (node.sync?.lastSync) facts.push(`last synced ${when(node.sync.lastSync)}`);
    const problems = [node.error, node.sync?.lastError].filter((p): p is string => !!p);
    const actions = h('div', { class: 'admin-row' });
    if (node.state !== 'unreachable') {
      actions.append(
        node.state === 'draining'
          ? button(
              'Resume',
              () =>
                void act(
                  `/api/admin/nodes/${node.id}/drain`,
                  { drain: false },
                  `${name} is taking sessions again.`,
                ),
            )
          : button('Drain', () => {
              if (
                confirm(
                  `Drain ${name}? It opens no new sessions; the ${node.sessions.length} open now carry on until they end.`,
                )
              )
                void act(
                  `/api/admin/nodes/${node.id}/drain`,
                  { drain: true },
                  `${name} is draining.`,
                );
            }),
      );
      if (clustered)
        actions.append(
          button(
            'Resync from scratch',
            () => {
              if (
                confirm(
                  `Resync ${name} from scratch? Its connections, users, shares, macros and settings are replaced by the other nodes' copies. Changes it made that no other node has yet are lost.`,
                )
              )
                void act(`/api/admin/nodes/${node.id}/resync`, undefined, `${name} was resynced.`);
            },
            'admin-btn admin-btn-quiet',
          ),
        );
    }
    return h(
      'section',
      { class: 'admin-card', 'aria-label': name, 'data-node': String(node.id) },
      h(
        'h2',
        {},
        name,
        node.self ? h('span', { class: 'admin-tag', text: 'this one' }) : null,
        h('span', { class: 'admin-tag', 'data-tone': tone, text: STATE_TEXT[node.state] }),
      ),
      facts.length ? h('p', { class: 'admin-intro', text: facts.join(' · ') }) : null,
      ...problems.map((p) => h('p', { class: 'admin-problem', text: p })),
      actions,
      node.state === 'unreachable' ? null : sessionsTable(node.sessions),
    );
  }

  async function load(): Promise<void> {
    const { nodes, clustered } = await api<{ nodes: Node[]; clustered: boolean }>(
      'GET',
      '/api/admin/nodes',
    );
    list.replaceChildren(...nodes.map((node) => card(node, clustered)));
  }

  const element = h(
    'section',
    { class: 'admin-panel' },
    h('p', {
      class: 'admin-intro',
      text: 'Each GreenGlow server and its sessions. Before restarting a server, drain it: it opens no new sessions, and the ones it has carry on until they end.',
    }),
    h(
      'div',
      { class: 'admin-row' },
      button('Refresh', () => void load(), 'admin-btn admin-btn-quiet'),
    ),
    list,
  );
  return { element, load };
}
