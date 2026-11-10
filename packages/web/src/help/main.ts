// The help page: the user guide, and for administrators the administrator guide, which the
// server sends only to them (/api/admin/guide). The page needs no sign-in for the user guide.

import '../style.css';
import './help.css';

type Guide = 'user' | 'admin';

const toc = document.querySelector<HTMLElement>('.help-toc')!;
const guides = document.querySelector<HTMLElement>('.help-guides')!;
const user = document.querySelector<HTMLElement>('#guide-user')!;
const admin = document.querySelector<HTMLElement>('#guide-admin')!;
let adminLoaded = false;

function look(): void {
  // The page follows the look chosen on the terminal page, kept in this browser.
  try {
    const saved = localStorage.getItem('greenglow.look');
    if (saved) document.documentElement.dataset.look = saved;
  } catch {
    // Storage blocked: the system setting decides.
  }
}

/** Whether the signed-in user is an administrator; false when signed out or unsure. */
async function isAdmin(): Promise<boolean> {
  try {
    const response = await fetch('/api/me', { credentials: 'same-origin' });
    if (!response.ok) return false;
    return ((await response.json()) as { isAdmin?: boolean }).isAdmin === true;
  } catch {
    return false;
  }
}

async function loadAdmin(): Promise<boolean> {
  if (adminLoaded) return true;
  const response = await fetch('/api/admin/guide', { credentials: 'same-origin' });
  if (!response.ok) return false;
  // The guide is the server's own HTML (never anything from the IBM i), sent to administrators
  // only. Parsed apart from the page, so nothing in it runs.
  const guide = new DOMParser().parseFromString(await response.text(), 'text/html');
  guide.querySelectorAll('script').forEach((script) => script.remove());
  admin.replaceChildren(...Array.from(guide.body.childNodes));
  adminLoaded = true;
  return true;
}

/** The contents list: each section heading of the guide showing. */
function contents(guide: HTMLElement): void {
  const list = document.createElement('ol');
  for (const section of guide.querySelectorAll<HTMLElement>('section[id]')) {
    const heading = section.querySelector('h2');
    if (!heading) continue;
    const item = document.createElement('li');
    const link = document.createElement('a');
    link.href = `#${section.id}`;
    link.textContent = heading.textContent;
    item.append(link);
    list.append(item);
  }
  const title = document.createElement('p');
  title.className = 'help-toc-title';
  title.textContent = 'Contents';
  toc.replaceChildren(title, list);
}

function show(guide: Guide): void {
  user.hidden = guide !== 'user';
  admin.hidden = guide !== 'admin';
  for (const link of guides.querySelectorAll<HTMLAnchorElement>('a[data-guide]'))
    link.setAttribute('aria-current', String(link.dataset.guide === guide));
  document.title =
    guide === 'admin' ? 'GreenGlow Connect administrator guide' : 'GreenGlow Connect help';
  contents(guide === 'admin' ? admin : user);
}

/** #admin, or a section of the administrator guide, shows that guide; anything else the user guide. */
async function route(administrator: boolean): Promise<void> {
  const hash = decodeURIComponent(location.hash.slice(1));
  const wantsAdmin = hash === 'admin' || (hash !== '' && hash.startsWith('admin-'));
  if (wantsAdmin && administrator && (await loadAdmin())) {
    show('admin');
    if (hash !== 'admin') document.getElementById(hash)?.scrollIntoView();
    else window.scrollTo(0, 0);
    return;
  }
  const wasAdmin = !admin.hidden;
  show('user');
  if (wasAdmin && hash === '') window.scrollTo(0, 0);
}

async function start(): Promise<void> {
  look();
  show('user');
  const administrator = await isAdmin();
  guides.hidden = !administrator;
  window.addEventListener('hashchange', () => void route(administrator));
  await route(administrator);
}

void start();
