// The page's own socket, /ws/user, not tied to a session: the server pushes the user's session
// list (the shape of GET /api/sessions) at once and whenever it changes, so the Disconnected
// card and the offers of newly shared sessions stay current without polling. A dropped socket
// (the server restarting, the network) is opened again with backoff, and the server sends the
// whole list again as soon as it is.

import type { SessionInfo } from './session.ts';

const FIRST_DELAY_MS = 1000;
const MAX_DELAY_MS = 30_000;

export class UserFeed {
  readonly #onList: (sessions: SessionInfo[]) => void;
  #delay = FIRST_DELAY_MS;
  #started = false;

  constructor(onList: (sessions: SessionInfo[]) => void) {
    this.#onList = onList;
  }

  start(): void {
    if (this.#started) return;
    this.#started = true;
    this.#open();
  }

  #open(): void {
    const scheme = location.protocol === 'https:' ? 'wss:' : 'ws:';
    const socket = new WebSocket(`${scheme}//${location.host}/ws/user`);
    socket.addEventListener('message', (event: MessageEvent<string>) => {
      const message = JSON.parse(event.data) as { type: string; sessions?: SessionInfo[] };
      if (message.type !== 'sessions' || !message.sessions) return;
      this.#delay = FIRST_DELAY_MS;
      this.#onList(message.sessions);
    });
    socket.addEventListener('close', () => {
      setTimeout(() => this.#open(), this.#delay);
      this.#delay = Math.min(this.#delay * 2, MAX_DELAY_MS);
    });
  }
}
