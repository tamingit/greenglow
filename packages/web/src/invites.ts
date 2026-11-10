// Sessions newly shared with this user, each offered in a card in the corner of the page:
// "Someone shared their session on X with you", with Join (Watch for view only) and Dismiss.
// The cards never take the focus, so keys typed into the screen keep going there; Alt+J joins
// the newest. Sessions shared before the page loaded aren't offered: the Disconnected card
// lists them.

import { button, h } from './admin/dom.ts';
import type { SessionInfo } from './session.ts';
import './invites.css';

export class Invites {
  readonly #join: (session: SessionInfo) => void;
  readonly #list: HTMLElement;
  /** Shared sessions already seen, offered or not; undefined until the first look. */
  #seen: Set<string> | undefined;
  readonly #cards = new Map<string, { card: HTMLElement; session: SessionInfo }>();

  constructor(join: (session: SessionInfo) => void) {
    this.#join = join;
    this.#list = h('div', { class: 'invites', role: 'status', 'aria-label': 'Shared with you' });
    document.body.append(this.#list);
  }

  /**
   * The user's sessions as the server lists them now, and the ones open in tabs: offers the
   * newly shared ones, and takes away the cards of any that ended or were unshared.
   */
  update(sessions: readonly SessionInfo[], open: ReadonlySet<string>): void {
    const shared = sessions.filter((s) => s.role !== 'owner');
    const ids = new Set(shared.map((s) => s.id));
    for (const id of [...this.#cards.keys()]) if (!ids.has(id) || open.has(id)) this.#remove(id);
    if (this.#seen === undefined) {
      this.#seen = ids;
      return;
    }
    for (const session of shared) {
      if (this.#seen.has(session.id)) continue;
      this.#seen.add(session.id);
      if (!open.has(session.id)) this.#offer(session);
    }
  }

  /** Joins the newest offer (Alt+J). Returns false when there is none. */
  joinNewest(): boolean {
    const newest = [...this.#cards.values()].at(-1);
    if (!newest) return false;
    this.#accept(newest.session);
    return true;
  }

  #offer(session: SessionInfo): void {
    const watch = session.role === 'view';
    const join = button(
      watch ? 'Watch' : 'Join',
      () => this.#accept(session),
      'invite-btn invite-join',
    );
    join.title = 'Alt+J';
    const dismiss = button('Dismiss', () => this.#remove(session.id), 'invite-btn');
    dismiss.setAttribute('aria-label', `Dismiss the session ${session.owner} shared`);
    const card = h(
      'div',
      { class: 'invite', 'data-session': session.id },
      h('p', {
        text:
          `${session.owner} shared their session on ${session.label} with you ` +
          `(${watch ? 'view only' : 'full access'}).`,
      }),
      h('div', { class: 'invite-row' }, join, dismiss),
    );
    this.#cards.set(session.id, { card, session });
    this.#list.append(card);
  }

  #accept(session: SessionInfo): void {
    this.#remove(session.id);
    this.#join(session);
  }

  #remove(id: string): void {
    this.#cards.get(id)?.card.remove();
    this.#cards.delete(id);
  }
}
