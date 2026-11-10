// The sharing panel, docked beside the screen (Alt+S, or the bar's Sharing button). Three
// parts, for the session in the active tab and for the user:
//
// - The keyboard: who has it, with Take control, or Return control for someone the session is
//   shared with who has it.
// - Shared with (the owner only): each person, with a light (green watching now, yellow in
//   GreenGlow but not watching, hollow not in GreenGlow), a keyboard button showing their
//   access that the owner clicks to switch View-Only and Full Access, and × to stop sharing with
//   them. + shares with someone else (the share dialog).
// - Sessions shared with you: each, with a light (green when open in this window), Join or
//   Leave, and × to take it off the list.

import { api } from './admin/api.ts';
import { button, h } from './admin/dom.ts';
import type { ControlState, SessionInfo } from './session.ts';
import './sharing.css';

type Access = 'view' | 'full';

interface Recipient {
  id: string;
  upn: string;
  name: string | null;
  access: Access;
  watching: boolean;
  keyboard: boolean;
  inGreenGlow: boolean;
}

interface Share {
  id: string;
  recipients: Recipient[];
}

/** One person across the session's shares (the owner may have named them in more than one). */
interface Person {
  upn: string;
  name: string;
  access: Access;
  watching: boolean;
  keyboard: boolean;
  inGreenGlow: boolean;
  /** Each share naming them, by its recipient path (the owner's view only). */
  paths: string[];
  /** The session's owner, in the list someone it is shared with sees. */
  owner?: boolean;
  /** The user looking. */
  you?: boolean;
}

/** /api/sessions/{id}/people: who is in a session, for anyone in it. */
interface People {
  owner: Omit<Person, 'access' | 'paths'>;
  people: (Omit<Person, 'paths'> & { you: boolean })[];
}

/** What the panel shows: the active tab's session, and the sessions shared with the user. */
export interface SharingView {
  session:
    | {
        id: string;
        role: SessionInfo['role'];
        /** A session is open and showing in the tab. */
        live: boolean;
        control: ControlState | undefined;
      }
    | undefined;
  shared: readonly SessionInfo[];
  /** Sessions open in this window's tabs. */
  open: ReadonlySet<string>;
}

export interface SharingActions {
  takeControl(): void;
  returnControl(): void;
  /** Opens the share dialog for the active session. */
  invite(): void;
  join(session: SessionInfo): void;
  leave(session: SessionInfo): void;
  /** The sessions shared with the user, or the people a session is shared with, changed. */
  changed(): void;
  /** The panel closed from the keyboard: the keys go back to the screen. */
  closed(): void;
}

const ICON_NS = 'http://www.w3.org/2000/svg';

/** The keyboard icon, crossed out for View-Only. */
function keyboardIcon(crossed: boolean): SVGSVGElement {
  const svg = document.createElementNS(ICON_NS, 'svg');
  svg.setAttribute('viewBox', '0 0 16 16');
  svg.setAttribute('aria-hidden', 'true');
  svg.classList.add('sharing-icon');
  const path = document.createElementNS(ICON_NS, 'path');
  path.setAttribute(
    'd',
    'M2 4.5h12a.5.5 0 0 1 .5.5v6a.5.5 0 0 1-.5.5H2a.5.5 0 0 1-.5-.5V5a.5.5 0 0 1 .5-.5ZM4 7h.01M6.5 7h.01M9 7h.01M11.5 7h.01M5 9.5h6' +
      (crossed ? 'M2 2l12 12' : ''),
  );
  svg.append(path);
  return svg;
}

function light(state: 'on' | 'near' | 'off', title: string): HTMLElement {
  return h('span', {
    class: 'sharing-light',
    'data-state': state,
    title,
    role: 'img',
    'aria-label': title,
  });
}

export class SharingPanel {
  readonly #root: HTMLElement;
  readonly #actions: SharingActions;
  readonly #live: HTMLElement;
  readonly #controlButton: HTMLButtonElement;
  readonly #controlText: HTMLElement;
  readonly #withSection: HTMLElement;
  readonly #invite: HTMLButtonElement;
  readonly #withCount: HTMLElement;
  readonly #withList: HTMLElement;
  readonly #yoursList: HTMLElement;
  readonly #status: HTMLElement;
  #view: SharingView = { session: undefined, shared: [], open: new Set() };
  #people: Person[] = [];
  /** Counts loads of the people list, so a slow answer for an earlier session is dropped. */
  #asked = 0;

  constructor(root: HTMLElement, actions: SharingActions) {
    this.#root = root;
    this.#actions = actions;
    this.#live = h('span', { class: 'sharing-live', text: 'Live', hidden: true });
    this.#controlButton = button('', () => this.#control(), 'sharing-control');
    this.#controlText = h('p', { class: 'sharing-holder', role: 'status' });
    this.#withCount = h('span', { class: 'sharing-count' });
    this.#withList = h('ul', { class: 'sharing-list', 'aria-label': 'Shared with' });
    const invite = button('+', () => actions.invite(), 'sharing-add');
    invite.setAttribute('aria-label', 'Share with someone');
    invite.title = 'Share with someone';
    this.#invite = invite;
    this.#withSection = h(
      'section',
      { class: 'sharing-section sharing-with', 'aria-labelledby': 'sharing-with-title' },
      h(
        'div',
        { class: 'sharing-section-head' },
        h('h3', { id: 'sharing-with-title', text: 'Shared with ' }, this.#withCount),
        invite,
      ),
      this.#withList,
    );
    this.#yoursList = h('ul', { class: 'sharing-list', 'aria-label': 'Sessions shared with you' });
    this.#status = h('p', { class: 'sharing-status', role: 'status' });
    const close = button('×', () => this.close(), 'sharing-close');
    close.setAttribute('aria-label', 'Close sharing panel');
    close.title = 'Close (Esc)';
    root.replaceChildren(
      h(
        'div',
        { class: 'sharing-head' },
        h('h2', { id: 'sharing-title', text: 'Sharing' }),
        this.#live,
        close,
      ),
      h(
        'section',
        { class: 'sharing-section sharing-keyboard', 'aria-label': 'Keyboard' },
        this.#controlButton,
        this.#controlText,
      ),
      this.#withSection,
      h(
        'section',
        { class: 'sharing-section sharing-yours', 'aria-labelledby': 'sharing-yours-title' },
        h('h3', { id: 'sharing-yours-title', text: 'Sessions shared with you' }),
        this.#yoursList,
      ),
      this.#status,
    );
    root.addEventListener('keydown', (event) => {
      if (event.key !== 'Escape') return;
      event.preventDefault();
      this.close();
      actions.closed();
    });
  }

  get isOpen(): boolean {
    return !this.#root.hidden;
  }

  open(): void {
    this.#root.hidden = false;
    this.#paint();
    void this.#loadPeople();
  }

  close(): void {
    this.#root.hidden = true;
  }

  /** Puts the keys in the panel: on Take or Return control if it is there, else its first button. */
  focus(): void {
    const shown = [...this.#root.querySelectorAll<HTMLElement>('button')].filter(
      (b) => b.offsetParent !== null,
    );
    (
      shown.find((b) => b === this.#controlButton) ??
      shown.find((b) => b.closest('.sharing-section'))
    )?.focus();
  }

  /** New facts from the page; the people list is asked for again while the panel is open. */
  update(view: SharingView): void {
    const sessionChanged = view.session?.id !== this.#view.session?.id;
    this.#view = view;
    if (sessionChanged) this.#people = [];
    if (!this.isOpen) return;
    this.#paint();
    void this.#loadPeople();
  }

  #paint(): void {
    this.#paintControl();
    this.#paintPeople();
    this.#paintYours();
  }

  #paintControl(): void {
    const session = this.#view.session;
    this.#live.hidden = !session?.live;
    const control = session?.live ? session.control : undefined;
    let action: 'take' | 'return' | undefined;
    if (!session?.live) this.#controlText.textContent = 'No session is open in this tab.';
    else if (session.role === 'view') this.#controlText.textContent = 'View only: you watch.';
    else if (!control || control.you) {
      this.#controlText.textContent = 'You have the keyboard.';
      if (control?.you && session.role !== 'owner') action = 'return';
    } else {
      this.#controlText.textContent = `${control.holder ?? 'No one'} has the keyboard.`;
      action = 'take';
    }
    this.#controlButton.hidden = action === undefined;
    this.#controlButton.dataset.action = action ?? '';
    this.#controlButton.textContent = action === 'return' ? 'Return control' : 'Take control';
  }

  #control(): void {
    if (this.#controlButton.dataset.action === 'return') this.#actions.returnControl();
    else this.#actions.takeControl();
  }

  async #loadPeople(): Promise<void> {
    const session = this.#view.session;
    const asked = ++this.#asked;
    if (!session?.live) {
      this.#people = [];
      this.#paintPeople();
      return;
    }
    if (session.role !== 'owner') {
      // Someone it is shared with: who else is in it, read only, the owner first.
      let found: People;
      try {
        found = await api<People>('GET', `/api/sessions/${encodeURIComponent(session.id)}/people`);
      } catch {
        return;
      }
      if (asked !== this.#asked) return;
      this.#people = [
        { ...found.owner, access: 'full', paths: [], owner: true },
        ...found.people.map((p) => ({ ...p, paths: [] })),
      ];
      this.#paintPeople();
      return;
    }
    let shares: Share[];
    try {
      shares = (
        await api<{ shares: Share[] }>(
          'GET',
          `/api/sessions/${encodeURIComponent(session.id)}/shares`,
        )
      ).shares;
    } catch {
      return;
    }
    if (asked !== this.#asked) return;
    const byUpn = new Map<string, Person>();
    for (const share of shares)
      for (const r of share.recipients) {
        const key = r.upn.toLowerCase();
        const person = byUpn.get(key) ?? {
          upn: r.upn,
          name: r.name || r.upn,
          access: 'view' as Access,
          watching: false,
          keyboard: false,
          inGreenGlow: false,
          paths: [],
        };
        if (r.access === 'full') person.access = 'full';
        person.watching ||= r.watching;
        person.keyboard ||= r.keyboard;
        person.inGreenGlow ||= r.inGreenGlow;
        person.paths.push(
          `/api/sessions/${encodeURIComponent(session.id)}/shares/${share.id}/recipients/${r.id}`,
        );
        byUpn.set(key, person);
      }
    this.#people = [...byUpn.values()].sort((a, b) => a.name.localeCompare(b.name));
    this.#paintPeople();
  }

  #paintPeople(): void {
    const session = this.#view.session;
    const mine = session?.role === 'owner';
    this.#withSection.hidden = !session?.live;
    this.#invite.hidden = !mine;
    this.#withCount.textContent = String(this.#people.filter((p) => !p.owner).length);
    this.#withList.replaceChildren(
      ...(this.#people.length === 0
        ? [h('li', { class: 'sharing-empty', text: 'No one yet. + shares this session.' })]
        : this.#people.map((person) =>
            mine ? this.#personRow(person) : this.#readOnlyRow(person),
          )),
    );
  }

  /** A person as someone the session is shared with sees them: no changes, the owner marked. */
  #readOnlyRow(person: Person): HTMLElement {
    const state = person.watching ? 'on' : person.inGreenGlow ? 'near' : 'off';
    const where = person.watching
      ? 'Watching now'
      : person.inGreenGlow
        ? 'In GreenGlow, not watching'
        : 'Not in GreenGlow';
    const role = person.owner ? 'Owner' : person.access === 'full' ? 'Full Access' : 'View-Only';
    const key = h(
      'span',
      {
        class: 'sharing-key sharing-key-shown',
        'data-state': person.keyboard ? 'keyboard' : person.access,
        role: 'img',
        'aria-label': `${role}${person.keyboard ? ', has the keyboard' : ''}`,
        title: `${role}${person.keyboard ? ': has the keyboard' : ''}`,
      },
      keyboardIcon(!person.owner && person.access === 'view'),
    );
    const name = `${person.name}${person.you ? ' (you)' : ''}`;
    return h(
      'li',
      { class: 'sharing-row', 'data-upn': person.upn, 'data-state': state },
      light(state, where),
      h(
        'span',
        { class: 'sharing-name', title: person.upn },
        h('span', { text: name }),
        ...(person.owner ? [h('span', { class: 'sharing-detail', text: 'Owner' })] : []),
      ),
      key,
    );
  }

  #personRow(person: Person): HTMLElement {
    const state = person.watching ? 'on' : person.inGreenGlow ? 'near' : 'off';
    const where = person.watching
      ? 'Watching now'
      : person.inGreenGlow
        ? 'In GreenGlow, not watching'
        : 'Not in GreenGlow';
    const access = person.access === 'full' ? 'Full Access' : 'View-Only';
    const toggle = h(
      'button',
      {
        type: 'button',
        class: 'sharing-key',
        'data-state': person.keyboard ? 'keyboard' : person.access,
        'aria-label':
          `${person.name}: ${access}${person.keyboard ? ', has the keyboard' : ''}. ` +
          (person.access === 'full' ? 'Make View-Only' : 'Give Full Access'),
        title:
          person.access === 'full'
            ? `${access}: click for View-Only`
            : `${access}: click for Full Access`,
      },
      keyboardIcon(person.access === 'view'),
    );
    toggle.addEventListener(
      'click',
      () => void this.#setAccess(person, person.access === 'full' ? 'view' : 'full'),
    );
    const remove = button('×', () => void this.#remove(person), 'sharing-x');
    remove.setAttribute('aria-label', `Stop sharing with ${person.upn}`);
    remove.title = 'Stop sharing with them';
    return h(
      'li',
      { class: 'sharing-row', 'data-upn': person.upn, 'data-state': state },
      light(state, where),
      h('span', { class: 'sharing-name', text: person.name, title: person.upn }),
      toggle,
      remove,
    );
  }

  async #setAccess(person: Person, access: Access): Promise<void> {
    try {
      for (const path of person.paths) await api('PATCH', path, { access });
      this.#status.textContent = `${person.name} now has ${access === 'full' ? 'Full Access' : 'View-Only'}.`;
    } catch (error) {
      this.#status.textContent = (error as Error).message;
    }
    await this.#loadPeople();
  }

  async #remove(person: Person): Promise<void> {
    try {
      for (const path of person.paths) await api('DELETE', path);
      this.#status.textContent = `${person.name} can no longer see this session.`;
    } catch (error) {
      this.#status.textContent = (error as Error).message;
    }
    await this.#loadPeople();
    this.#actions.changed();
  }

  #paintYours(): void {
    const shared = this.#view.shared;
    this.#yoursList.replaceChildren(
      ...(shared.length === 0
        ? [h('li', { class: 'sharing-empty', text: 'None now.' })]
        : shared.map((session) => this.#yourRow(session))),
    );
  }

  #yourRow(session: SessionInfo): HTMLElement {
    const here = this.#view.open.has(session.id);
    const watch = session.role === 'view';
    const go = here
      ? button('Leave', () => this.#actions.leave(session), 'sharing-btn')
      : button(watch ? 'Watch' : 'Join', () => this.#actions.join(session), 'sharing-btn');
    go.setAttribute(
      'aria-label',
      `${here ? 'Leave' : watch ? 'Watch' : 'Join'} ${session.owner}’s session on ${session.label}`,
    );
    const forget = button('×', () => void this.#forget(session), 'sharing-x');
    forget.setAttribute(
      'aria-label',
      `Take ${session.owner}’s session on ${session.label} off your list`,
    );
    forget.title = 'Take it off your list';
    return h(
      'li',
      { class: 'sharing-row', 'data-session': session.id, 'data-state': here ? 'on' : 'off' },
      light(here ? 'on' : 'off', here ? 'Open in this window' : 'Not open here'),
      h(
        'span',
        { class: 'sharing-name' },
        h('span', { text: session.owner }),
        h('span', {
          class: 'sharing-detail',
          text: `${session.label}${watch ? ' · view only' : ''}`,
        }),
      ),
      go,
      forget,
    );
  }

  async #forget(session: SessionInfo): Promise<void> {
    try {
      await api('DELETE', `/api/sessions/${encodeURIComponent(session.id)}/shares/me`);
      this.#status.textContent = `${session.owner}’s session is off your list.`;
    } catch (error) {
      this.#status.textContent = (error as Error).message;
    }
    this.#actions.changed();
  }
}
