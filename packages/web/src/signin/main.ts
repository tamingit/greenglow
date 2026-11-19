// The password sign-in page, for local accounts and IBM i user profiles. /auth/signin sends
// people here under those methods; under any other method this page hands back to
// /auth/signin, which knows where to go. Asked by the server to choose a new password (a local
// account an administrator set, or an expired IBM i password), it shows the new-password
// fields and sends the change with the sign-in. The server's wording is shown as it is.

import '../style.css';
import '../admin/admin.css';
import './signin.css';
import { basePath } from '../urls.ts';

interface MethodInfo {
  method: string;
  system: string | null;
  breakglass: boolean;
}

/** A sign-in the server refused, with its error code and its wording. */
class Refusal extends Error {
  readonly status: number;
  readonly error: string | undefined;

  constructor(message: string, status: number, error?: string) {
    super(message);
    this.status = status;
    this.error = error;
  }
}

const title = document.querySelector<HTMLElement>('#signin-title')!;
const intro = document.querySelector<HTMLElement>('#signin-intro')!;
const message = document.querySelector<HTMLElement>('#signin-message')!;
const form = document.querySelector<HTMLFormElement>('#signin-form')!;
const userLabel = document.querySelector<HTMLElement>('#signin-user-label')!;
const change = document.querySelector<HTMLFieldSetElement>('#signin-change')!;
const changeHint = document.querySelector<HTMLElement>('#signin-change-hint')!;
const submit = document.querySelector<HTMLButtonElement>('#signin-submit')!;
const breakglass = document.querySelector<HTMLDetailsElement>('#breakglass')!;
const breakglassForm = document.querySelector<HTMLFormElement>('#breakglass-form')!;
const breakglassSubmit = document.querySelector<HTMLButtonElement>('#breakglass-submit')!;

const returnUrl = new URLSearchParams(location.search).get('returnUrl') ?? basePath();
let method: 'local' | 'ibmi' = 'local';

function control(owner: HTMLFormElement, name: string): HTMLInputElement {
  return owner.querySelector<HTMLInputElement>(`[name="${name}"]`)!;
}

function say(text: string, error: boolean): void {
  message.textContent = text;
  message.dataset.error = String(error);
  message.hidden = false;
}

function point(input: HTMLInputElement): void {
  input.setAttribute('aria-invalid', 'true');
  input.focus();
  input.select();
}

function clearInvalid(): void {
  for (const input of document.querySelectorAll<HTMLInputElement>('[aria-invalid]'))
    input.removeAttribute('aria-invalid');
}

/** Posts a sign-in; resolves with where to go, or rejects with the server's refusal. */
async function post(path: string, body: unknown): Promise<string> {
  let response: Response;
  try {
    response = await fetch(path, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify(body),
    });
  } catch {
    throw new Refusal('The server can’t be reached. Check the connection and try again.', 0);
  }
  let data: { redirect?: string; error?: string; message?: string } = {};
  try {
    data = (await response.json()) as typeof data;
  } catch {
    // No JSON body: the status says enough.
  }
  if (response.ok && data.redirect) return data.redirect;
  const fallback =
    response.status === 429
      ? 'Too many tries. Wait a minute and try again.'
      : `Signing in failed (${response.status}).`;
  throw new Refusal(data.message ?? fallback, response.status, data.error);
}

function showChange(text: string): void {
  change.hidden = false;
  change.disabled = false;
  submit.textContent = 'Change password and sign in';
  say(text, false);
  control(form, 'newPassword').focus();
}

/** The local account rules, as the server checks them; an IBM i applies its own. */
function newPasswordProblem(password: string, current: string): string | null {
  if (method === 'local') {
    if (password.length < 12) return 'The new password must have at least 12 characters.';
    if (password.length > 128) return 'The new password can have at most 128 characters.';
  }
  if (password === current) return 'The new password must differ from the current one.';
  return null;
}

async function signIn(): Promise<void> {
  clearInvalid();
  const user = control(form, 'user');
  const password = control(form, 'password');
  if (!user.value.trim()) {
    say(method === 'ibmi' ? 'Type your IBM i user.' : 'Type your user name.', true);
    point(user);
    return;
  }
  if (!password.value) {
    say('Type your password.', true);
    point(password);
    return;
  }
  const changing = !change.hidden;
  const next = control(form, 'newPassword');
  const confirm = control(form, 'newPasswordConfirm');
  if (changing) {
    const problem = newPasswordProblem(next.value, password.value);
    if (problem) {
      say(problem, true);
      point(next);
      return;
    }
    if (next.value !== confirm.value) {
      say('The new passwords don’t match.', true);
      point(confirm);
      return;
    }
  }

  submit.disabled = true;
  try {
    const target = changing
      ? await post('auth/password/change', {
          user: user.value.trim(),
          password: password.value,
          newPassword: next.value,
          returnUrl,
        })
      : await post('auth/password', {
          user: user.value.trim(),
          password: password.value,
          returnUrl,
        });
    say('Signed in.', false);
    location.assign(target);
  } catch (refusal) {
    submit.disabled = false;
    if (!(refusal instanceof Refusal)) throw refusal;
    switch (refusal.error) {
      case 'password-change-required':
      case 'password-expired':
        showChange(refusal.message);
        return;
      case 'new-password-refused':
        say(refusal.message, true);
        point(next);
        return;
      case 'sign-in-failed':
        say(refusal.message, true);
        password.value = '';
        point(password);
        return;
      default:
        say(refusal.message, true);
    }
  }
}

async function signInBreakglass(): Promise<void> {
  clearInvalid();
  const password = control(breakglassForm, 'breakglassPassword');
  if (!password.value) {
    say('Type the breakglass password.', true);
    point(password);
    return;
  }
  breakglassSubmit.disabled = true;
  try {
    const target = await post('auth/breakglass', { password: password.value, returnUrl });
    say('Signed in.', false);
    location.assign(target);
  } catch (refusal) {
    breakglassSubmit.disabled = false;
    if (!(refusal instanceof Refusal)) throw refusal;
    say(refusal.message, true);
    if (refusal.error === 'sign-in-failed') {
      password.value = '';
      point(password);
    }
  }
}

form.addEventListener('submit', (event) => {
  event.preventDefault();
  void signIn();
});
breakglassForm.addEventListener('submit', (event) => {
  event.preventDefault();
  void signInBreakglass();
});
breakglass.addEventListener('toggle', () => {
  if (breakglass.open) control(breakglassForm, 'breakglassPassword').focus();
});

async function start(): Promise<void> {
  let info: MethodInfo;
  try {
    const response = await fetch('auth/method');
    if (!response.ok) throw new Error(String(response.status));
    info = (await response.json()) as MethodInfo;
  } catch {
    say('The server can’t be reached. Reload the page to try again.', true);
    return;
  }
  if (info.method !== 'local' && info.method !== 'ibmi') {
    // Another method signs in elsewhere: /auth/signin knows where.
    location.replace(`auth/signin?returnUrl=${encodeURIComponent(returnUrl)}`);
    return;
  }
  method = info.method;
  if (method === 'ibmi') {
    title.textContent = 'Sign in with your IBM i user';
    intro.textContent = info.system
      ? `Use your IBM i user and password on ${info.system}.`
      : 'Use your IBM i user and password.';
    userLabel.textContent = 'IBM i user';
    changeHint.textContent = 'Your IBM i’s password rules apply.';
  } else {
    intro.textContent = 'Use the user name and password an administrator gave you.';
    changeHint.textContent = 'Requirements: 12 to 128 characters.';
  }
  form.hidden = false;
  breakglass.hidden = !info.breakglass;
  control(form, 'user').focus();
}

void start();
