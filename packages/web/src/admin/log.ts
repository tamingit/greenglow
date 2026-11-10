// The connection log: every host session and every browser attached to one, with the user,
// their address, the system and when it started and ended. Rows still open are current. It
// also records each certificate an administrator trusts for a connection, or stops trusting,
// each print session (printer output): its IBM i user and how many files were opened, and
// each node an administrator drained, resumed or resynced. With several nodes, each row says
// which node wrote it; rows from other nodes arrive within a minute.

import { api } from './api.ts';
import { button, h, notify, when } from './dom.ts';

interface Row {
  id: string;
  /** The node that wrote it; 0 for rows from before nodes. */
  node: number;
  kind: 'session' | 'frontend' | 'certificate' | 'printer' | 'node';
  sessionId: string;
  upn: string | null;
  clientIp: string | null;
  connection: string;
  device: string | null;
  role: string | null;
  startedAt: string;
  endedAt: string | null;
  endReason: string | null;
  current: boolean;
  ibmUser: string | null;
  title: string | null;
  filesOpened: number | null;
}

const COLUMNS = [
  'User',
  'IP address',
  'System',
  'Node',
  'What',
  'Connected',
  'Disconnected',
  'Device',
  'IBM i user',
  'Screen',
  'Why it ended',
] as const;

function duration(row: Row): string {
  const end = row.endedAt ? new Date(row.endedAt) : new Date();
  const minutes = Math.round((end.getTime() - new Date(row.startedAt).getTime()) / 60000);
  return minutes < 60 ? `${minutes} min` : `${Math.floor(minutes / 60)} h ${minutes % 60} min`;
}

function what(row: Row): string {
  if (row.kind === 'session') return 'Host session';
  if (row.kind === 'printer') {
    const files = row.filesOpened ?? 0;
    return `Printer output (${files} ${files === 1 ? 'file' : 'files'} opened)`;
  }
  if (row.kind === 'node') return 'Server node';
  if (row.kind === 'certificate')
    return row.role === 'removed' ? 'Certificate no longer trusted' : 'Certificate trusted';
  return `Browser (${row.role ?? 'owner'})`;
}

function cells(row: Row): string[] {
  return [
    row.upn ?? '—',
    row.clientIp ?? '—',
    row.connection,
    row.node ? String(row.node) : '—',
    what(row),
    when(row.startedAt),
    row.current ? 'Still connected' : when(row.endedAt),
    row.device ?? '—',
    row.ibmUser ?? '',
    row.title ?? '',
    row.endReason ?? '',
  ];
}

function csvField(value: string): string {
  // Leading =, +, - or @ would run as a formula in a spreadsheet.
  const safe = /^[=+\-@]/.test(value) ? `'${value}` : value;
  return /[",\n]/.test(safe) ? `"${safe.replace(/"/g, '""')}"` : safe;
}

export function logPanel(): { element: HTMLElement; load(): Promise<void> } {
  const query = h('input', {
    class: 'admin-input',
    id: 'log-q',
    type: 'search',
    placeholder: 'User, IP, device or IBM i user',
  });
  const kind = h(
    'select',
    { class: 'admin-select', id: 'log-kind' },
    h('option', { value: '', text: 'Everything' }),
    h('option', { value: 'session', text: 'Host sessions' }),
    h('option', { value: 'frontend', text: 'Browsers' }),
    h('option', { value: 'certificate', text: 'Certificates trusted' }),
    h('option', { value: 'printer', text: 'Printer output' }),
    h('option', { value: 'node', text: 'Nodes drained or resynced' }),
  );
  const from = h('input', { class: 'admin-input', id: 'log-from', type: 'datetime-local' });
  const to = h('input', { class: 'admin-input', id: 'log-to', type: 'datetime-local' });
  const current = h('input', { type: 'checkbox', id: 'log-current' });
  const body = h('tbody');
  const more = button('Show older', () => void fetchPage(true), 'admin-btn admin-btn-quiet');
  const summary = h('p', { class: 'admin-intro', role: 'status' });
  let rows: Row[] = [];
  let next: string | null = null;

  function params(older: boolean): string {
    const p = new URLSearchParams({ limit: '100' });
    if (query.value.trim()) p.set('q', query.value.trim());
    if (kind.value) p.set('kind', kind.value);
    if (from.value) p.set('from', new Date(from.value).toISOString());
    if (to.value) p.set('to', new Date(to.value).toISOString());
    if (current.checked) p.set('current', 'true');
    if (older && next !== null) p.set('before', next);
    return p.toString();
  }

  async function fetchPage(older: boolean): Promise<void> {
    try {
      const page = await api<{ rows: Row[]; next: string | null }>(
        'GET',
        `/api/admin/log?${params(older)}`,
      );
      rows = older ? [...rows, ...page.rows] : page.rows;
      next = page.next;
      render();
    } catch (error) {
      notify((error as Error).message, true);
    }
  }

  function render(): void {
    body.replaceChildren(
      ...rows.map((row) =>
        h(
          'tr',
          { 'data-current': String(row.current) },
          ...cells(row).map((text, i) =>
            h(
              'td',
              { title: i === 6 && !row.current ? `${duration(row)}` : false },
              i === 6 && row.current
                ? h('span', { class: 'admin-tag', 'data-tone': 'live', text: 'current' })
                : text,
            ),
          ),
        ),
      ),
    );
    const open = rows.filter((r) => r.current && r.kind === 'session').length;
    summary.textContent = `${rows.length} row${rows.length === 1 ? '' : 's'} shown, ${open} host session${open === 1 ? '' : 's'} open now.`;
    more.hidden = next === null;
  }

  function download(): void {
    const text = [COLUMNS.join(','), ...rows.map((row) => cells(row).map(csvField).join(','))].join(
      '\r\n',
    );
    const link = h('a', {
      href: URL.createObjectURL(new Blob([text], { type: 'text/csv' })),
      download: `greenglow-connection-log-${new Date().toISOString().slice(0, 10)}.csv`,
    });
    link.click();
    URL.revokeObjectURL(link.href);
  }

  for (const input of [kind, from, to, current])
    input.addEventListener('change', () => void fetchPage(false));
  let timer: ReturnType<typeof setTimeout> | undefined;
  query.addEventListener('input', () => {
    clearTimeout(timer);
    timer = setTimeout(() => void fetchPage(false), 250);
  });

  const field = (input: HTMLElement, label: string) =>
    h(
      'div',
      { class: 'admin-field' },
      h('label', { for: input.id, class: 'admin-label', text: label }),
      input,
    );

  const element = h(
    'section',
    { class: 'admin-panel' },
    h('p', {
      class: 'admin-intro',
      text: 'Who connected, from where, to which system and when. Open rows are current connections.',
    }),
    h(
      'div',
      { class: 'admin-row' },
      field(query, 'Search'),
      field(kind, 'Show'),
      field(from, 'From'),
      field(to, 'To'),
      h('label', { class: 'admin-check' }, current, 'Current only'),
      button('Refresh', () => void fetchPage(false), 'admin-btn admin-btn-quiet'),
      button('Download CSV', download, 'admin-btn admin-btn-quiet'),
    ),
    summary,
    h(
      'div',
      { class: 'admin-table-wrap' },
      h(
        'table',
        { class: 'admin-table', 'aria-label': 'Connection log' },
        h('thead', {}, h('tr', {}, ...COLUMNS.map((c) => h('th', { scope: 'col', text: c })))),
        body,
      ),
    ),
    more,
  );
  return { element, load: () => fetchPage(false) };
}
