// Why a session left this page, in words: for the alert, the status line and the Disconnected
// card. The server closes a browser's socket with a short reason (`host closed`, `host error
// ConnectionReset`, `server shutting down`, a sentence for a share); a socket that dropped
// without one is a lost connection to the server.

export interface Ended {
  /** A few words, for the alert's title and the status line. */
  title: string;
  /** A sentence or two on what happened and what to do. */
  message: string;
  /** Something went wrong, as opposed to a sign-off or an owner's choice. */
  error: boolean;
}

/** The reason this page gives when the user disconnects or closes the tab. */
export const USER_DISCONNECTED = 'user disconnected';

/**
 * Why the session on `label` ended, or undefined when the user ended it here (Disconnect, or
 * closing its tab), which needs no alert.
 */
export function describeEnd(code: number, reason: string, label: string): Ended | undefined {
  if (reason === USER_DISCONNECTED) return undefined;
  if (reason === 'host closed')
    return {
      title: 'Session ended',
      message: `${label} ended the session, usually because you signed off.`,
      error: false,
    };
  const hostError = /^host error (.+)$/.exec(reason);
  if (hostError)
    return {
      title: 'Connection to the IBM i lost',
      message:
        `The connection to ${label} was lost or refused (${hostError[1]}). ` +
        'Connect again; if it keeps happening, the IBM i or the network may be down.',
      error: true,
    };
  if (reason === 'server shutting down')
    return {
      title: 'GreenGlow restarted',
      message: `The GreenGlow server stopped, which ended the session on ${label}. Connect again in a moment.`,
      error: true,
    };
  if (reason === 'server error')
    return {
      title: 'GreenGlow error',
      message: `Something went wrong on the GreenGlow server, which ended the session on ${label}.`,
      error: true,
    };
  if (reason === 'ended by the user')
    return {
      title: 'Ended elsewhere',
      message: `The session on ${label} was ended from another tab or browser.`,
      error: false,
    };
  if (reason === 'browser too slow')
    return {
      title: 'Connection too slow',
      message: `This page fell too far behind the session on ${label}, so the server let it go. The session is still open: join it again.`,
      error: true,
    };
  if (/opened in another browser/.test(reason))
    return { title: 'Opened elsewhere', message: reason, error: false };
  if (/^The owner (left|isn't in)/.test(reason))
    return { title: 'Owner left', message: reason, error: false };
  if (/stopped sharing/.test(reason))
    return { title: 'Sharing stopped', message: reason, error: false };
  if (/browsers? attached, the most allowed/.test(reason))
    return { title: 'Session full', message: reason, error: true };
  if (code === 1000)
    return {
      title: 'Session ended',
      message: `The session on ${label} ended${reason ? ` (${reason})` : ''}.`,
      error: false,
    };
  return {
    title: 'Connection to GreenGlow lost',
    message:
      `This page lost its connection to the GreenGlow server (${code}${reason ? `: ${reason}` : ''}). ` +
      'Connect again, or join the session again from the list if it is still open.',
    error: true,
  };
}
