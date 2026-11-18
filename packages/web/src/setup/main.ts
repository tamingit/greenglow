// First-run setup: chooses how people sign in, once. Everything goes to POST /auth/setup in one
// request, and the server checks all of it before saving anything. Errors name a field, which
// is focused; the server's wording is shown as it is.

import '../style.css';
import '../admin/admin.css';
import './setup.css';
import { ApiError, api } from '../admin/api.ts';
import { h } from '../admin/dom.ts';
import { brandName, brandShortName } from '../brand.ts';
import { basePath } from '../urls.ts';

type Method = 'entra' | 'local' | 'ibmi';

interface SetupDone {
  redirect: string;
  warning?: string | null;
}

const form = document.querySelector<HTMLFormElement>('#setup-form')!;
const message = document.querySelector<HTMLElement>('#setup-message')!;

const METHODS: { value: Method; label: string; detail: string }[] = [
  {
    value: 'entra',
    label: 'Microsoft Entra ID',
    detail: 'Staff sign in with their work accounts. Needs an app registration.',
  },
  {
    value: 'local',
    label: 'Local accounts',
    detail: 'Accounts kept here, with passwords. For a single administrator or a test server.',
  },
  {
    value: 'ibmi',
    label: 'IBM i user profiles',
    detail: 'People sign in with their IBM i user and password, checked on an IBM i you name.',
  },
];

function input(
  name: string,
  type = 'text',
  extra: Partial<Record<string, string | boolean>> = {},
): HTMLInputElement {
  return h('input', { class: 'admin-input', name, type, autocomplete: 'off', ...extra });
}

function field(label: string, control: HTMLElement, hint?: string): HTMLElement {
  return h(
    'label',
    { class: 'admin-field' },
    h('span', { text: label }),
    control,
    hint ? h('span', { class: 'setup-hint', text: hint }) : null,
  );
}

function section(method: Method, legend: string, ...children: HTMLElement[]): HTMLFieldSetElement {
  return h(
    'fieldset',
    { 'data-method': method, hidden: true },
    h('legend', { text: legend }),
    ...children,
  );
}

// The breakglass password, then one section per method; only the chosen one is sent.
const breakglass = h(
  'fieldset',
  {},
  h('legend', { text: 'Breakglass administrator' }),
  field(
    'Breakglass password',
    input('breakglassPassword', 'password', { autocomplete: 'new-password', required: true }),
    'The way in if the sign-in method stops working. Requirements: 12 to 128 characters.',
  ),
  field(
    'Confirm breakglass password',
    input('breakglassConfirm', 'password', { autocomplete: 'new-password', required: true }),
  ),
);

const entra = section(
  'entra',
  'Microsoft Entra ID',
  field(
    'Directory (tenant) ID',
    input('entra.tenantId', 'text', { required: true }),
    'From the app registration’s overview.',
  ),
  field('Application (client) ID', input('entra.clientId', 'text', { required: true })),
  field(
    'Client secret',
    input('entra.clientSecret', 'password', { autocomplete: 'new-password', required: true }),
    'The secret’s value, not its ID. The redirect URI is this server’s public address with /auth/callback added.',
  ),
);

const local = section(
  'local',
  'Local accounts',
  field(
    'First administrator user name',
    input('local.user', 'text', { required: true }),
    'Letters, digits, dots, dashes, underscores or @. Up to 64 characters.',
  ),
  field('Display name', input('local.name', 'text')),
  field(
    'Password',
    input('local.password', 'password', { autocomplete: 'new-password', required: true }),
    'Requirements: 12 to 128 characters.',
  ),
  field(
    'Confirm password',
    input('local.passwordConfirm', 'password', { autocomplete: 'new-password', required: true }),
  ),
);

const ibmi = section(
  'ibmi',
  'IBM i user profiles',
  field(
    'Name for this IBM i',
    input('ibmi.newConnection.name', 'text', { required: true }),
    'Lowercase letters, digits and dashes. It names the system in the picker and in the log.',
  ),
  field(
    'Address',
    input('ibmi.newConnection.url', 'text', {
      required: true,
      placeholder: 'tls://ibmi.example.com',
    }),
    'tls:// for verified TLS (port 992 by default). tcp:// is plain Telnet, for test systems only.',
  ),
  field('Label (optional)', input('ibmi.newConnection.label', 'text')),
  field(
    'IBM i user',
    input('ibmi.user', 'text', { required: true }),
    'This user becomes the first administrator.',
  ),
  field(
    'IBM i password',
    input('ibmi.password', 'password', { autocomplete: 'current-password', required: true }),
    'Checked on the IBM i now. It is not stored.',
  ),
);

const methods = h(
  'fieldset',
  { class: 'setup-methods' },
  h('legend', { text: 'Sign-in method' }),
  ...METHODS.map((m, i) =>
    h(
      'label',
      { class: 'setup-method' },
      h('input', {
        type: 'radio',
        name: 'method',
        value: m.value,
        ...(i === 0 ? { checked: true } : {}),
      }),
      h('span', {}, h('strong', { text: m.label }), h('small', { text: m.detail })),
    ),
  ),
);

// This page is at the app's base path: the address with it, without the trailing slash.
const publicUrl = input('publicUrl', 'text', {
  required: true,
  value: location.origin + basePath().replace(/\/$/, ''),
});
const submit = h('button', {
  type: 'submit',
  class: 'admin-btn',
  text: 'Save and open administration',
});

form.append(
  field(
    'Address of this server',
    publicUrl,
    `The address people open ${brandShortName()} at, with http:// or https://, the port if it isn’t the default, and the path if it is under one. Pre-filled with the address this page is open at.`,
  ),
  breakglass,
  methods,
  entra,
  local,
  ibmi,
  h('div', { class: 'setup-actions' }, submit),
);

function selected(): Method {
  const checked = form.querySelector<HTMLInputElement>('input[name="method"]:checked');
  return (checked?.value as Method | undefined) ?? 'entra';
}

function showSection(): void {
  const method = selected();
  for (const element of form.querySelectorAll<HTMLFieldSetElement>('fieldset[data-method]')) {
    element.hidden = element.dataset.method !== method;
    for (const control of element.querySelectorAll<HTMLInputElement>('input'))
      control.disabled = element.hidden;
  }
}

function say(text: string, error: boolean): void {
  message.textContent = text;
  message.dataset.error = String(error);
  message.hidden = false;
}

function clearInvalid(): void {
  for (const control of form.querySelectorAll<HTMLInputElement>('[aria-invalid]'))
    control.removeAttribute('aria-invalid');
}

/** Focuses the field the server named, if the form has one with that name. */
function pointAt(field: string): void {
  const control = form.querySelector<HTMLInputElement>(`[name="${field}"], [name$=".${field}"]`);
  if (!control) return;
  control.setAttribute('aria-invalid', 'true');
  control.focus();
}

function value(name: string): string {
  return form.querySelector<HTMLInputElement>(`[name="${name}"]`)?.value ?? '';
}

function body(): Record<string, unknown> {
  const method = selected();
  const base = {
    publicUrl: address(value('publicUrl')),
    breakglassPassword: value('breakglassPassword'),
    method,
  };
  if (method === 'entra')
    return {
      ...base,
      entra: {
        tenantId: value('entra.tenantId').trim(),
        clientId: value('entra.clientId').trim(),
        clientSecret: value('entra.clientSecret'),
      },
    };
  if (method === 'local')
    return {
      ...base,
      local: {
        user: value('local.user').trim(),
        name: value('local.name').trim(),
        password: value('local.password'),
      },
    };
  return {
    ...base,
    ibmi: {
      newConnection: {
        name: value('ibmi.newConnection.name').trim(),
        url: value('ibmi.newConnection.url').trim(),
        label: value('ibmi.newConnection.label').trim() || undefined,
      },
      user: value('ibmi.user').trim(),
      password: value('ibmi.password'),
    },
  };
}

/** The first problem with what's typed, as a message and the field to point at, or null. */
/**
 * An address typed in the form, as the server keeps it (http or https, the host, and a path when
 * the app is under one, with no trailing slash), or null when it isn't one.
 */
function address(text: string): string | null {
  try {
    const url = new URL(text.trim());
    if (url.protocol !== 'http:' && url.protocol !== 'https:') return null;
    if (url.search || url.hash || url.username || url.password) return null;
    return url.origin + url.pathname.replace(/\/+$/, '');
  } catch {
    return null;
  }
}

/** The breakglass and local password rules, as the server checks them (its lengths count UTF-16 units too). */
function passwordProblem(password: string): string | null {
  if (password.length < 12) return 'The password must have at least 12 characters.';
  if (password.length > 128) return 'The password can have at most 128 characters.';
  return null;
}

function checkLocally(): { field: string; text: string } | null {
  if (address(value('publicUrl')) === null)
    return {
      field: 'publicUrl',
      text: 'The address is like http://localhost:8080 or https://greenglow.example.com, with a path only when the app is under one, such as https://example.com/greenglow.',
    };
  const breakglass = passwordProblem(value('breakglassPassword'));
  if (breakglass) return { field: 'breakglassPassword', text: breakglass };
  if (value('breakglassPassword') !== value('breakglassConfirm'))
    return { field: 'breakglassConfirm', text: 'The breakglass passwords don’t match.' };
  if (selected() === 'local') {
    const local = passwordProblem(value('local.password'));
    if (local) return { field: 'local.password', text: local };
    if (value('local.password') !== value('local.passwordConfirm'))
      return { field: 'local.passwordConfirm', text: 'The passwords don’t match.' };
  }
  return null;
}

function done(outcome: SetupDone): void {
  form.hidden = true;
  const continueButton = h('a', {
    class: 'admin-btn',
    href: outcome.redirect,
    text: 'Continue to administration',
  });
  say(outcome.warning ?? `${brandName()} is set up.`, false);
  message.append(h('p', {}, continueButton));
  setTimeout(() => location.assign(outcome.redirect), outcome.warning ? 8000 : 800);
}

form.addEventListener('change', showSection);
form.addEventListener('submit', (event) => {
  event.preventDefault();
  void save();
});

async function save(): Promise<void> {
  clearInvalid();
  const problem = checkLocally();
  if (problem) {
    say(problem.text, true);
    pointAt(problem.field);
    return;
  }
  submit.disabled = true;
  try {
    const outcome = await api<SetupDone>('POST', 'auth/setup', body());
    done(outcome);
  } catch (error) {
    if (error instanceof ApiError) {
      say(error.message, true);
      if (error.code) pointAt(error.code);
      if (error.status === 409)
        message.append(h('p', {}, h('a', { href: 'admin.html', text: 'Go to administration' })));
    } else say('Setup could not be saved. Check the connection to the server and try again.', true);
    submit.disabled = false;
  }
}

showSection();
