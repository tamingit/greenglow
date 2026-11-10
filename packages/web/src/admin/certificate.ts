// A tls:// connection's certificate on its card: whether it is verified, and by what. Check
// certificate shows what the IBM i presents; Trust keeps its CA for this connection only,
// alongside the server's own CA (Relay__CaPem). When the IBM i doesn't send its root, the root
// is pasted in. A certificate for another name is trusted only after the administrator accepts
// that name, which is then the name it must have.

import { ApiError, api } from './api.ts';
import { button, h, notify, when } from './dom.ts';

export interface Trust {
  subject: string;
  sha256: string;
  notAfter: string;
  /** The name accepted in place of the host, if any. */
  name: string | null;
  by: string | null;
  at: string | null;
}

interface Certificate {
  subject: string;
  issuer: string;
  notBefore: string;
  notAfter: string;
  sha256: string;
  selfSigned: boolean;
  isCa: boolean;
}

interface Check {
  certificates: Certificate[];
  names: string[];
  host: string;
  hostMatches: boolean;
  /** The fingerprint of the CA Trust would keep, when the IBM i sent its root. */
  trustable: string | null;
  verified: boolean;
}

export interface CertificateConnection {
  id: string;
  label: string;
  verified: boolean;
  trust: Trust | null;
}

/** A SHA-256 fingerprint as pairs of hex digits, the way certificate viewers show it. */
export function fingerprint(hex: string): string {
  return hex.match(/../g)?.join(':') ?? hex;
}

/**
 * The card's certificate section. `serverCa`: the server has its own CA. `changed` reloads
 * the cards after a trust is added or removed.
 */
export function certificateSection(
  connection: CertificateConnection,
  serverCa: boolean,
  changed: () => Promise<void>,
): HTMLElement {
  const path = `/api/admin/connections/${connection.id}/certificate`;
  const result = h('div', { class: 'admin-cert-check', hidden: true });

  async function trust(body: { sha256?: string; pem?: string; acceptName?: string }) {
    try {
      await api('POST', `${path}/trust`, body);
    } catch (error) {
      if (!(error instanceof ApiError) || error.code !== 'name-mismatch') throw error;
      const names = (error.data as { names?: string[] } | undefined)?.names ?? [];
      const name = names[0];
      if (
        name === undefined ||
        !confirm(
          `${error.message} Accept it anyway? Connections to ${connection.label} will then accept ` +
            `a certificate for ${name} from this CA.`,
        )
      )
        return false;
      await api('POST', `${path}/trust`, { ...body, acceptName: name });
    }
    return true;
  }

  async function act(action: () => Promise<boolean>, done: string): Promise<void> {
    try {
      if (!(await action())) return;
      notify(done);
      await changed();
    } catch (error) {
      notify((error as Error).message, true);
    }
  }

  function showCheck(check: Check): void {
    const top = check.certificates[check.certificates.length - 1];
    const pem = h('textarea', {
      class: 'admin-input admin-pem',
      id: `c${connection.id}-pem`,
      rows: '6',
      spellcheck: 'false',
      placeholder: '-----BEGIN CERTIFICATE-----',
    });
    result.replaceChildren(
      h(
        'ol',
        { class: 'admin-cert-chain', 'aria-label': 'Certificates the IBM i sent' },
        ...check.certificates.map((c, i) =>
          h(
            'li',
            {},
            h('strong', {
              text:
                i === 0
                  ? `${c.subject} (the IBM i)`
                  : `${c.subject}${c.selfSigned ? ' (root)' : ''}`,
            }),
            h('span', { text: `Issued by ${c.issuer}` }),
            h('span', { text: `Valid ${when(c.notBefore)} to ${when(c.notAfter)}` }),
            h('code', { text: `SHA-256 ${fingerprint(c.sha256)}` }),
          ),
        ),
      ),
      h('p', {
        class: 'admin-intro',
        text: check.hostMatches
          ? `It names ${check.host}.`
          : `It is for ${check.names.join(', ') || 'no name'}, not ${check.host}.`,
      }),
      check.verified
        ? h('p', { class: 'admin-intro', text: 'It is already verified.' })
        : check.trustable && top
          ? h(
              'div',
              { class: 'admin-row' },
              button(
                `Trust ${top.subject}`,
                () =>
                  void act(
                    () => trust({ sha256: check.trustable ?? undefined }),
                    `${connection.label} now trusts ${top.subject}.`,
                  ),
              ),
            )
          : h(
              'div',
              { class: 'admin-field admin-field-wide' },
              h('label', {
                for: pem.id,
                class: 'admin-label',
                text: 'The IBM i doesn’t send its root CA. Paste the root CA (PEM) to trust it:',
              }),
              pem,
              h(
                'div',
                { class: 'admin-row' },
                button(
                  'Trust this CA',
                  () =>
                    void act(
                      () => trust({ pem: pem.value }),
                      `${connection.label} now trusts the pasted CA.`,
                    ),
                ),
              ),
            ),
    );
    result.hidden = false;
  }

  const t = connection.trust;
  const status = t
    ? `Trusted for this connection: ${t.subject} (SHA-256 ${fingerprint(t.sha256)}), until ${when(t.notAfter)}.` +
      (t.name ? ` The certificate must be for ${t.name}.` : '') +
      ` Trusted by ${t.by ?? 'an administrator'} on ${when(t.at)}.` +
      (serverCa ? ' The server’s own CA is trusted too.' : '')
    : serverCa
      ? 'Verified with the server’s CA (Relay__CaPem).'
      : 'Not verified: any certificate is accepted. Check it, and trust its CA to verify it.';
  return h(
    'div',
    { class: 'admin-cert' },
    h('h3', { text: 'Certificate' }),
    h('p', { class: 'admin-intro', 'data-verified': String(connection.verified), text: status }),
    h(
      'div',
      { class: 'admin-row' },
      button(
        'Check certificate',
        () =>
          void api<Check>('POST', `${path}/check`).then(showCheck, (error: unknown) =>
            notify((error as Error).message, true),
          ),
        'admin-btn admin-btn-quiet',
      ),
      t
        ? button(
            'Stop trusting',
            () => {
              if (
                !confirm(
                  `Stop trusting ${t.subject} for ${connection.label}? ` +
                    (serverCa
                      ? 'Only the server’s CA will be trusted.'
                      : 'Any certificate will be accepted, unverified.'),
                )
              )
                return;
              void act(
                () => api('DELETE', path).then(() => true),
                `${connection.label} no longer trusts ${t.subject}.`,
              );
            },
            'admin-btn admin-btn-danger',
          )
        : null,
    ),
    result,
  );
}
