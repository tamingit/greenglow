// The Files side panel: download a file from the integrated file system (IFS) of the system of
// the tab showing, by its path. The user signs on to the IBM i's file server here, with a saved
// login or their IBM i user and password, so the IBM i decides what they may read. A typed
// password goes to the server once and is cleared from the form at once, never kept in the page
// or in storage. The panel checks a path first (its size, or why it can't be read), then lets the
// browser download it, so a large file streams to disk instead of through the page.

import { ApiError, api } from './admin/api.ts';
import { button, h } from './admin/dom.ts';
import type { PrinterSystem } from './printer.ts';
import './files.css';

/** The system the panel is for: the one of the tab showing. */
export type FilesSystem = PrinterSystem;

/** A saved IBM i login, as /api/files/{connection} lists them. */
export interface SavedLogin {
  id: string;
  ibmUser: string;
  remembered: boolean;
  /** Its password is to hand: it signs on without asking. */
  ready: boolean;
}

interface FilesState {
  signedOn: boolean;
  ibmUser: string | null;
  suggestedUser: string | null;
  canRemember: boolean;
  logins: SavedLogin[];
}

interface FileInfo {
  path: string;
  name: string;
  size: number;
}

/** A size for people: bytes, then KB, MB and GB (1024s), one decimal under ten. */
export function sizeText(bytes: number): string {
  if (bytes < 0) return '';
  if (bytes < 1024) return bytes === 1 ? '1 byte' : `${bytes} bytes`;
  const units = ['KB', 'MB', 'GB', 'TB'];
  let value = bytes / 1024;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit++;
  }
  return `${value < 10 ? value.toFixed(1) : Math.round(value)} ${units[unit]}`;
}

/** Why a path can't be asked for, or undefined when it can: it must be absolute. */
export function pathProblem(path: string): string | undefined {
  if (path === '') return 'Type the path of a file, such as /home/USER/report.csv.';
  if (!path.startsWith('/')) return 'A path starts with /, from the root of the IFS.';
  return undefined;
}

/** The recent paths: the newest first, each once, at most `most`. */
export function remember(recent: readonly string[], path: string, most = 5): string[] {
  return [path, ...recent.filter((p) => p !== path)].slice(0, most);
}

export class FilesPanel {
  readonly #root: HTMLElement;
  readonly #system: HTMLElement;
  readonly #body: HTMLElement;
  readonly #status: HTMLElement;
  #current: FilesSystem | undefined;
  #recent: string[] = [];
  #ibmUser = '';
  /** Counts what the panel shows, so a late answer for an earlier system is dropped. */
  #generation = 0;

  constructor(root: HTMLElement, closed: () => void) {
    this.#root = root;
    this.#system = h('p', { class: 'printer-system' });
    this.#body = h('div', { class: 'printer-body' });
    this.#status = h('p', { class: 'printer-status', role: 'status' });
    const close = button('×', closed, 'printer-close');
    close.setAttribute('aria-label', 'Close files panel');
    close.title = 'Close (Alt+I)';
    root.replaceChildren(
      h('div', { class: 'printer-head' }, h('h2', { id: 'files-title', text: 'Files' }), close),
      this.#system,
      this.#body,
      this.#status,
    );
    root.addEventListener('keydown', (event) => {
      if (event.key !== 'Escape') return;
      event.preventDefault();
      closed();
    });
  }

  /** Shows the panel for a system: its sign-on, or the path to download. */
  async show(system: FilesSystem | undefined, force = false): Promise<void> {
    if (!force && system?.name === this.#current?.name && this.#body.childElementCount > 0) return;
    if (system?.name !== this.#current?.name) this.#recent = [];
    this.#current = system;
    const generation = ++this.#generation;
    this.#say('');
    if (!system) {
      this.#system.textContent = '';
      this.#body.replaceChildren(
        h('p', { class: 'printer-empty', text: 'Choose a system in this tab first.' }),
      );
      return;
    }
    this.#system.textContent = system.label;
    try {
      const state = await api<FilesState>('GET', this.#path());
      if (generation !== this.#generation) return;
      if (state.signedOn && state.ibmUser) this.#download(state.ibmUser);
      else this.#signOnForm(state, system.ibmUser ?? state.suggestedUser ?? '');
    } catch (error) {
      if (generation === this.#generation) this.#fail(error);
    }
  }

  /** Puts the focus in the panel: the first field or button. */
  focus(): void {
    this.#root.querySelector<HTMLElement>('input, button:not(.printer-close)')?.focus();
  }

  #say(text: string, error = false): void {
    this.#status.textContent = text;
    this.#status.dataset.error = String(error);
  }

  #fail(error: unknown): void {
    if (error instanceof ApiError && error.code === 'signon-needed') {
      void this.show(this.#current, true).then(() => this.#say(error.message, true));
      return;
    }
    this.#say((error as Error).message, true);
  }

  #path(rest = ''): string {
    return `/api/files/${encodeURIComponent(this.#current?.name ?? '')}${rest}`;
  }

  #signOnForm(state: FilesState, user: string): void {
    const generation = this.#generation;
    const ready = state.logins.filter((login) => login.ready);
    const quick = ready.map((login) => {
      const go = button(
        `Sign on as ${login.ibmUser}`,
        () => void this.#signOn({ login: login.id }, generation),
        'printer-btn printer-primary',
      );
      go.title = 'With your saved login';
      return go;
    });
    const saved = state.logins.find((login) => !login.ready);
    const userBox = h('input', {
      class: 'printer-input',
      id: 'files-user',
      type: 'text',
      autocomplete: 'off',
      spellcheck: 'false',
      maxlength: '10',
    });
    userBox.value = user || saved?.ibmUser || '';
    const passwordBox = h('input', {
      class: 'printer-input',
      id: 'files-password',
      type: 'password',
      autocomplete: 'off',
    });
    const save = h('input', { type: 'checkbox', id: 'files-save' });
    const keep = h('input', { type: 'checkbox', id: 'files-remember', disabled: true });
    save.addEventListener('change', () => {
      keep.disabled = !save.checked;
      if (!save.checked) keep.checked = false;
    });
    const submit = h('button', {
      type: 'submit',
      class: 'printer-btn printer-primary',
      text: 'Sign on',
    });
    const form = h(
      'form',
      { class: 'printer-form', 'aria-label': 'Sign on to files' },
      h('p', {
        class: 'printer-intro',
        text: 'Sign on with your IBM i user and password to download files from the IFS. You can read only what your profile may read.',
      }),
      h('label', { for: 'files-user', text: 'IBM i user' }),
      userBox,
      h('label', { for: 'files-password', text: 'Password' }),
      passwordBox,
      h('label', { class: 'files-check' }, save, ' Save this login for next time'),
      state.canRemember
        ? h('label', { class: 'files-check' }, keep, ' Remember the password too')
        : null,
      submit,
      h('p', {
        class: 'printer-note',
        text: 'A wrong password counts toward disabling your IBM i profile, as at the sign-on screen.',
      }),
    );
    form.addEventListener('submit', (event) => {
      event.preventDefault();
      // Out of the page at once: the field is cleared before the answer comes.
      const password = passwordBox.value;
      passwordBox.value = '';
      submit.disabled = true;
      void this.#signOn(
        { user: userBox.value.trim(), password, save: save.checked, remember: keep.checked },
        generation,
      ).finally(() => {
        submit.disabled = false;
      });
    });
    this.#body.replaceChildren(
      ...(quick.length > 0
        ? [h('div', { class: 'files-logins', 'aria-label': 'Saved logins' }, ...quick)]
        : []),
      form,
    );
    if (quick.length > 0) quick[0]?.focus();
    else (userBox.value ? passwordBox : userBox).focus();
  }

  async #signOn(body: Record<string, unknown>, generation: number): Promise<void> {
    this.#say('Signing on…');
    try {
      const reply = await api<{ ibmUser: string }>('POST', this.#path('/signon'), body);
      if (generation !== this.#generation) return;
      this.#download(reply.ibmUser);
      this.#say(`Signed on as ${reply.ibmUser}.`);
    } catch (error) {
      if (generation !== this.#generation) return;
      this.#say((error as Error).message, true);
      this.#root.querySelector<HTMLInputElement>('#files-password')?.focus();
    }
  }

  /** The signed-on view: the path box, Download, and this panel's recent paths. */
  #download(ibmUser: string): void {
    this.#ibmUser = ibmUser;
    const pathBox = h('input', {
      class: 'printer-input',
      id: 'files-path',
      type: 'text',
      autocomplete: 'off',
      spellcheck: 'false',
      placeholder: `/home/${ibmUser}/report.csv`,
    });
    const go = h('button', {
      type: 'submit',
      class: 'printer-btn printer-primary',
      text: 'Download',
    });
    const form = h(
      'form',
      { class: 'printer-form', 'aria-label': 'Download a file' },
      h('label', { for: 'files-path', text: 'Path of a file in the IFS' }),
      pathBox,
      go,
    );
    form.addEventListener('submit', (event) => {
      event.preventDefault();
      go.disabled = true;
      void this.#fetch(pathBox.value.trim()).finally(() => {
        go.disabled = false;
      });
    });
    const signOff = button('Sign off', () => void this.#signOff(), 'printer-btn');
    const recent = this.#recent.length
      ? h(
          'div',
          { class: 'files-recent' },
          h('p', { class: 'printer-note', text: 'Recent' }),
          h(
            'ul',
            { class: 'printer-files', 'aria-label': 'Recent downloads' },
            ...this.#recent.map((path) => {
              const again = button(path, () => void this.#fetch(path), 'printer-file files-again');
              again.title = 'Download again';
              return h('li', {}, again);
            }),
          ),
        )
      : null;
    this.#body.replaceChildren(
      h(
        'div',
        { class: 'printer-bar' },
        h('span', { class: 'printer-user', text: `Signed on as ${ibmUser}` }),
        signOff,
      ),
      form,
      ...(recent ? [recent] : []),
    );
    pathBox.focus();
  }

  /** Checks a path, then has the browser download it. */
  async #fetch(path: string): Promise<void> {
    const problem = pathProblem(path);
    if (problem) {
      this.#say(problem, true);
      return;
    }
    const generation = this.#generation;
    this.#say(`Checking ${path}…`);
    let info: FileInfo;
    try {
      info = await api<FileInfo>('GET', this.#path(`/info?path=${encodeURIComponent(path)}`));
    } catch (error) {
      if (generation === this.#generation) this.#fail(error);
      return;
    }
    if (generation !== this.#generation) return;
    // The browser saves it: an attachment from this origin, with the sign-in cookie.
    const link = h('a', {
      href: this.#path(`/download?path=${encodeURIComponent(info.path)}`),
      download: info.name,
      hidden: true,
    });
    document.body.append(link);
    link.click();
    link.remove();
    this.#recent = remember(this.#recent, info.path);
    this.#download(this.#ibmUser);
    this.#say(`Downloading ${info.name} (${sizeText(info.size)}).`);
  }

  async #signOff(): Promise<void> {
    try {
      await api('DELETE', this.#path('/signon'));
    } catch (error) {
      this.#fail(error);
      return;
    }
    await this.show(this.#current, true);
    this.#say('Signed off.');
  }
}
