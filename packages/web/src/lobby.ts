// The sessions a user can go back to, shown on the Disconnected card: their own sessions the
// server is still holding, and sessions shared with them. Each row says when it was opened,
// the IBM i user signed on, the screen it is on and how many browsers show it now (In use or
// Available when only one may). With more than one to join, each has a checkbox, and Join
// selected opens them in tabs at once. The user's own session with no room left can be taken
// over: the browser showing it lets it go.

import type { SessionInfo } from './session.ts';

export interface LobbyActions {
  join(session: SessionInfo): void;
  /** Joins one of the user's own sessions that has no room, sending away a browser showing it. */
  takeOver(session: SessionInfo): void;
  /** Joins several, each in its own tab. */
  joinAll(sessions: SessionInfo[]): void;
  /** Ends one of the user's own sessions on the server. */
  end(session: SessionInfo): void;
}

function cell(tag: 'td' | 'th', text: string, className?: string): HTMLElement {
  const node = document.createElement(tag);
  node.textContent = text;
  if (className) node.className = className;
  return node;
}

function action(
  label: string,
  className: string,
  click: () => void,
  title?: string,
): HTMLButtonElement {
  const node = document.createElement('button');
  node.type = 'button';
  node.className = className;
  node.textContent = label;
  if (title) node.title = title;
  node.addEventListener('click', click);
  return node;
}

/** When a session was opened: the time today, the date and time otherwise. */
export function opened(iso: string, now = new Date()): string {
  const when = new Date(iso);
  const today = when.toDateString() === now.toDateString();
  return today
    ? when.toLocaleTimeString(undefined, { hour: 'numeric', minute: '2-digit' })
    : when.toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' });
}

/** The table of sessions, or null when there are none. */
export function lobby(
  sessions: readonly SessionInfo[],
  maxFrontends: number,
  actions: LobbyActions,
): HTMLElement | null {
  if (sessions.length === 0) return null;
  const table = document.createElement('table');
  table.className = 'lobby-table';
  const caption = document.createElement('caption');
  caption.textContent = sessions.length === 1 ? 'Your open session' : 'Your open sessions';
  // Only the owner's own browsers count toward the limit; people it is shared with don't.
  const isFull = (session: SessionInfo) =>
    session.role === 'owner' && session.ownerFrontends >= maxFrontends;
  const joinable = sessions.filter((session) => !isFull(session));
  const choosing = joinable.length > 1;
  const boxes = new Map<HTMLInputElement, SessionInfo>();
  const joinSelected = action('Join selected', 'lobby-join-all', () =>
    actions.joinAll([...boxes].filter(([box]) => box.checked).map(([, session]) => session)),
  );
  joinSelected.disabled = true;
  const all = checkbox('Select all sessions');
  const sync = () => {
    const checked = [...boxes.keys()].filter((box) => box.checked).length;
    joinSelected.disabled = checked === 0;
    joinSelected.textContent = checked > 1 ? `Join ${checked} selected` : 'Join selected';
    all.checked = checked === boxes.size;
    all.indeterminate = checked > 0 && checked < boxes.size;
  };
  all.addEventListener('change', () => {
    for (const box of boxes.keys()) box.checked = all.checked;
    sync();
  });
  const head = document.createElement('tr');
  if (choosing) {
    const corner = cell('th', '', 'lobby-pick');
    corner.append(all);
    head.append(corner);
  }
  const one = maxFrontends === 1;
  for (const name of ['Opened', 'System', 'IBM i user', 'Screen', one ? 'Status' : 'Browsers', ''])
    head.append(cell('th', name));
  const thead = document.createElement('thead');
  thead.append(head);
  const body = document.createElement('tbody');
  for (const session of sessions) {
    const row = document.createElement('tr');
    row.dataset.session = session.id;
    const full = isFull(session);
    const shared = session.role !== 'owner';
    // The buttons sit in a box of their own in the cell, which never narrows below them.
    const actionsCell = cell('td', '', 'lobby-actions');
    const buttons = document.createElement('div');
    buttons.className = 'lobby-buttons';
    actionsCell.append(buttons);
    if (full && !shared)
      buttons.append(
        action(
          'Take over',
          'lobby-join',
          () => actions.takeOver(session),
          one
            ? 'Show it here; the browser showing it now lets it go'
            : 'Show it here; the first browser that showed it lets it go',
        ),
      );
    else
      buttons.append(
        action(shared ? (session.role === 'view' ? 'Watch' : 'Join') : 'Join', 'lobby-join', () =>
          actions.join(session),
        ),
      );
    if (!shared)
      buttons.append(
        action('End', 'lobby-end', () => actions.end(session), 'End this session on the server'),
      );
    if (choosing) {
      const pick = cell('td', '', 'lobby-pick');
      const box = checkbox(
        `Select the session on ${session.label} opened ${opened(session.createdAt)}`,
      );
      if (full) box.disabled = true;
      else boxes.set(box, session);
      box.addEventListener('change', sync);
      pick.append(box);
      row.append(pick);
    }
    row.append(
      cell('td', opened(session.createdAt), 'lobby-opened'),
      cell('td', shared ? `${session.label} (shared by ${session.owner})` : session.label),
      cell('td', session.ibmUser ?? (session.signedOn ? '?' : 'Not signed on')),
      cell('td', session.title || '—', 'lobby-screen'),
      cell(
        'td',
        shared
          ? String(session.frontends)
          : one
            ? session.ownerFrontends > 0
              ? 'In use'
              : 'Available'
            : `${session.ownerFrontends} of ${maxFrontends}`,
        'lobby-count',
      ),
      actionsCell,
    );
    body.append(row);
  }
  table.append(caption, thead, body);
  if (!choosing) return table;
  const foot = document.createElement('tfoot');
  const last = document.createElement('tr');
  const end = cell('td', '', 'lobby-foot');
  (end as HTMLTableCellElement).colSpan = 7;
  end.append(joinSelected);
  last.append(end);
  foot.append(last);
  table.append(foot);
  return table;
}

function checkbox(label: string): HTMLInputElement {
  const box = document.createElement('input');
  box.type = 'checkbox';
  box.setAttribute('aria-label', label);
  return box;
}
