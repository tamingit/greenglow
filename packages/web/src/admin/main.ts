// The administration page: tabs over the admin API. Only administrators get here; anyone
// else is sent back to the terminal.

import '../style.css';
import './admin.css';
import { api } from './api.ts';
import { h, notify } from './dom.ts';
import { adminsPanel } from './admins.ts';
import { connectionsPanel, settingsPanel } from './connections.ts';
import { logPanel } from './log.ts';
import { nodesPanel } from './nodes.ts';

interface Me {
  id: string;
  upn: string;
  name: string;
  isAdmin: boolean;
}

interface Panel {
  element: HTMLElement;
  load(): Promise<void>;
}

const tabs = document.querySelector<HTMLElement>('.admin-tabs')!;
const panels = document.querySelector<HTMLElement>('#admin-panels')!;

function look(): void {
  // The page follows the look chosen on the terminal page, kept in this browser.
  try {
    const saved = localStorage.getItem('greenglow.look');
    if (saved) document.documentElement.dataset.look = saved;
  } catch {
    // Storage blocked: the system setting decides.
  }
}

async function start(): Promise<void> {
  look();
  const me = await api<Me>('GET', '/api/me');
  if (!me.isAdmin) {
    location.replace('/');
    return;
  }
  document.querySelector('#admin-user')!.textContent = me.upn;
  const all: [string, string, Panel][] = [
    ['connections', 'Connections', connectionsPanel()],
    ['log', 'Connection log', logPanel()],
    ['nodes', 'Nodes', nodesPanel()],
    ['admins', 'Administrators', adminsPanel(me)],
    ['settings', 'Settings', settingsPanel()],
  ];
  const buttons = all.map(([id, label, panel]) => {
    const tab = h('button', {
      type: 'button',
      role: 'tab',
      id: `tab-${id}`,
      class: 'admin-tab',
      'aria-controls': `panel-${id}`,
      text: label,
    });
    panel.element.id = `panel-${id}`;
    panel.element.setAttribute('role', 'tabpanel');
    panel.element.setAttribute('aria-labelledby', tab.id);
    tab.addEventListener('click', () => void show(id));
    return tab;
  });
  tabs.replaceChildren(...buttons);
  panels.replaceChildren(...all.map(([, , panel]) => panel.element));

  let current: string | undefined;
  async function show(id: string): Promise<void> {
    // The tab already showing stays as it is: reloading it would wipe what is being typed.
    if (id === current) return;
    current = id;
    for (const [key, , panel] of all) {
      const selected = key === id;
      panel.element.hidden = !selected;
      document.querySelector(`#tab-${key}`)?.setAttribute('aria-selected', String(selected));
    }
    history.replaceState(null, '', `#${id}`);
    try {
      await all.find(([key]) => key === id)?.[2].load();
    } catch (error) {
      notify(String((error as Error).message), true);
    }
  }
  // A link or Back to another tab's address switches to it.
  window.addEventListener('hashchange', () => {
    const id = location.hash.slice(1);
    if (all.some(([key]) => key === id)) void show(id);
  });
  const wanted = location.hash.slice(1);
  await show(all.some(([key]) => key === wanted) ? wanted : all[0][0]);
}

void start().catch((error: unknown) => notify(String(error), true));
