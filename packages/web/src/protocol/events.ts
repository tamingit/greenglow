// Session events the server forwards to the page ({type: 'event', event}): the engine's
// events that the page shows (HostSession.ForwardedEvents in server/src/GreenGlow.Server/
// Sessions), with the field names of the C# records in server/src/GreenGlow.Engine.

/** A Startup Response Record, described (RFC 4777 section 10.4). */
export interface StartupResult {
  /** Four characters, for example I902. */
  code: string;
  systemName: string;
  deviceName: string;
  ok: boolean;
  message: string;
}

/** What a send was for. */
export type OutboundKind = 'negotiation' | 'query-reply' | 'protocol' | 'host-read' | 'input';

export type ConnectionEvent =
  /** Telnet negotiation is complete and 5250 records can flow. */
  | { type: 'negotiated' }
  | { type: 'startup'; result: StartupResult }
  /** The requested device is held by another session. */
  | { type: 'device-in-use'; deviceName: string }
  /** The IBM i started the session on a different device from the one requested. */
  | { type: 'device-mismatch'; requested: string; actual: string }
  | { type: 'query-answered' }
  /** A key waits in the type-ahead buffer while the host waits for input. */
  | { type: 'keyboard-stuck' }
  /** A negative response went to the host; `code` is from table 13-26. */
  | { type: 'negative-response'; code: number }
  /** Something the engine doesn't handle. */
  | { type: 'unsupported'; what: string }
  | { type: 'data-stream-error'; message: string }
  | { type: 'protocol-error'; message: string }
  | { type: 'alarm' };

/** A negative response code as the manual writes it, for example 1005 0142. */
export function formatResponseCode(code: number): string {
  const hex = (code >>> 0).toString(16).toUpperCase().padStart(8, '0');
  return `${hex.slice(0, 4)} ${hex.slice(4)}`;
}
