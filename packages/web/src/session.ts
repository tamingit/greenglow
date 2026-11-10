// One 5250 session in the page, held by the server, which runs the engine and the Telnet
// connection: the page opens it through /api/sessions, attaches over /ws/session/<id>, gets
// each screen as JSON and sends keys as JSON. The server sends only keys this page sends,
// and only while this page holds the keyboard.

import {
  decodeSnapshot,
  type ConnectionEvent,
  type Key,
  type ScreenSnapshot,
  type WireSnapshot,
} from './protocol/index.ts';

export const TERMINAL_TYPE = 'IBM-3477-FC';

/** Who holds the keyboard of a session. */
export interface ControlState {
  /** This page may type. */
  you: boolean;
  /** The name of whoever holds it, if anyone. */
  holder: string | null;
  /** Browsers attached to the session, this one included. */
  frontends: number;
}

export interface SessionListener {
  /** A new screen, with its title as the server reads it (for a tab's name). */
  /** `signedOn`: past the sign-on screen, as the server, which saw the session start, knows. */
  screen(snapshot: ScreenSnapshot, title: string, signedOn: boolean): void;
  event(event: ConnectionEvent): void;
  closed(code: number, reason: string): void;
  /** Who holds the keyboard changed. */
  control?(state: ControlState): void;
  /** How much of a paste went in. */
  pasted?(result: PasteResult): void;
  /** The owner changed this browser's access to their share. */
  role?(role: 'full' | 'view'): void;
}

/** The most a paste may hold, in UTF-16 units as the server counts: more than a 27 x 132 screen. */
export const MAX_PASTE = 4000;

/**
 * What the server typed of a paste. `stopped` says why it stopped short: a key the screen
 * refused (a protected position, a field's rules), the keyboard locking (an auto-enter field
 * sent the screen), or lines that would fall below the screen.
 */
export interface PasteResult {
  typed: number;
  of: number;
  stopped: 'refused' | 'keyboard' | 'screen-end' | null;
}

/** What the server says about a session; never the host name. */
export interface SessionInfo {
  id: string;
  target: string;
  label: string;
  secure: boolean;
  createdAt: string;
  device: string | null;
  ibmUser: string | null;
  title: string;
  /** Browsers showing it now, the owner's and shared people's. */
  frontends: number;
  /** The owner's own browsers showing it: what the browsers-per-session limit counts. */
  ownerFrontends: number;
  /** One of the owner's browsers shows it now. */
  ownerPresent: boolean;
  signedOn: boolean;
  owner: string;
  role: 'owner' | 'full' | 'view';
  /** For a session shared with this user: the link's token, to attach with. */
  shareToken?: string | null;
}

/** The server refused to open a session; `code` says why (limit, server-full, draining, ...). */
export class SessionRefused extends Error {
  readonly code: string;

  constructor(code: string, message: string) {
    super(message);
    this.code = code;
  }
}

export interface Session {
  readonly open: boolean;
  /** The latest screen, or undefined before the first. */
  readonly screen: ScreenSnapshot | undefined;
  /** Live sessions: the server's id. */
  readonly id: string | undefined;
  key(key: Key): void;
  /** Types text from the cursor, as the user's own keys; the server stops at a refused key. */
  paste(text: string): void;
  /** Leaves the session. */
  close(): void;
}

/** Opens a new session on a named target. `spare` asks for a spare device name. */
export async function openSession(target: string, spare = false): Promise<SessionInfo> {
  const response = await fetch('/api/sessions', {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ target, spare }),
  });
  // 503: the server is draining before a restart.
  if (response.status === 409 || response.status === 404 || response.status === 503) {
    const { error, message } = (await response.json()) as { error: string; message: string };
    throw new SessionRefused(error, message);
  }
  if (!response.ok) throw new Error(`Opening a session failed: ${response.status}`);
  return (await response.json()) as SessionInfo;
}

/** A session held by the server, attached over a WebSocket. */
export class RemoteSession implements Session {
  readonly id: string;
  readonly #socket: WebSocket;
  readonly #listener: SessionListener;
  #screen: ScreenSnapshot | undefined;
  #title = '';
  #open = true;
  #closeReason = '';

  /**
   * `share` is the token of the link a recipient came in through. `takeOver` has the server
   * send away the owner's browsers showing it elsewhere to make room for this one.
   */
  constructor(id: string, listener: SessionListener, share?: string | null, takeOver = false) {
    this.id = id;
    this.#listener = listener;
    const scheme = location.protocol === 'https:' ? 'wss:' : 'ws:';
    const query = share ? `?share=${encodeURIComponent(share)}` : takeOver ? '?takeover=1' : '';
    this.#socket = new WebSocket(
      `${scheme}//${location.host}/ws/session/${encodeURIComponent(id)}${query}`,
    );
    this.#socket.addEventListener('message', (event: MessageEvent<string>) =>
      this.#message(event.data),
    );
    this.#socket.addEventListener('close', (event) => {
      this.#open = false;
      listener.closed(event.code, this.#closeReason || event.reason);
    });
  }

  get open(): boolean {
    return this.#open;
  }

  get screen(): ScreenSnapshot | undefined {
    return this.#screen;
  }

  /** The latest screen's title. */
  get title(): string {
    return this.#title;
  }

  key(key: Key): void {
    if (this.#open && this.#socket.readyState === WebSocket.OPEN)
      this.#socket.send(JSON.stringify({ type: 'key', key }));
  }

  /** Asks for the keyboard; the server gives it to a full-access page. */
  paste(text: string): void {
    if (this.open && this.#socket.readyState === WebSocket.OPEN)
      this.#socket.send(JSON.stringify({ type: 'paste', text: text.slice(0, MAX_PASTE) }));
  }

  takeControl(): void {
    if (this.#socket.readyState === WebSocket.OPEN)
      this.#socket.send(JSON.stringify({ type: 'take-control' }));
  }

  /** Gives the keyboard back to the owner, from someone the session is shared with. */
  returnControl(): void {
    if (this.#socket.readyState === WebSocket.OPEN)
      this.#socket.send(JSON.stringify({ type: 'return-control' }));
  }

  close(): void {
    this.#closeReason = 'user disconnected';
    this.#socket.close(1000, 'user disconnected');
  }

  #message(data: string): void {
    const message = JSON.parse(data) as ServerMessage;
    switch (message.type) {
      case 'screen':
        this.#screen = decodeSnapshot(message.snapshot);
        this.#title = message.title ?? '';
        this.#listener.screen(this.#screen, this.#title, message.signedOn ?? false);
        return;
      case 'event':
        this.#listener.event(message.event);
        return;
      case 'control':
        this.#listener.control?.(message);
        return;
      case 'pasted':
        this.#listener.pasted?.(message);
        return;
      case 'role':
        this.#listener.role?.(message.role);
        return;
      case 'closed':
        this.#closeReason = message.reason;
        return;
      default:
        return;
    }
  }
}

type ServerMessage =
  | { type: 'hello'; sessionId: string; role: string }
  | { type: 'screen'; title?: string; signedOn?: boolean; snapshot: WireSnapshot }
  | { type: 'event'; event: ConnectionEvent }
  | ({ type: 'control' } & ControlState)
  | { type: 'refused'; reason: string }
  | ({ type: 'pasted' } & PasteResult)
  | { type: 'role'; role: 'full' | 'view' }
  | { type: 'closed'; reason: string };
