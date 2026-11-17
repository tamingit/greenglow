// The Files side panel: browse the integrated file system (IFS) of the system of the tab showing,
// and download a file from it. The user signs on to the IBM i's file server here, with a saved
// login or their IBM i user and password, so the IBM i decides what they may see and read. A
// typed password goes to the server once and is cleared from the form at once, never kept in the
// page or in storage. A download checks the file first (its size, or why it can't be read), then
// lets the browser save it, so a large file streams to disk instead of through the page.

import { ApiError, api } from './admin/api.ts';
import { button, h } from './admin/dom.ts';
import { nextRow, type PrinterSystem } from './printer.ts';
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

/** A folder's entry, as /api/files/{connection}/list lists it: folders first, then by name. */
export interface FolderEntry {
  name: string;
  folder: boolean;
  /** Null for a folder. */
  size: number | null;
  modified: string;
}

interface Listing {
  path: string;
  truncated: boolean;
  entries: FolderEntry[];
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
  if (path === '') return 'Type a path, such as /home/USER or /home/USER/report.csv.';
  if (!path.startsWith('/')) return 'A path starts with /, from the root of the IFS.';
  return undefined;
}

/** The recent paths: the newest first, each once, at most `most`. */
export function remember(recent: readonly string[], path: string, most = 5): string[] {
  return [path, ...recent.filter((p) => p !== path)].slice(0, most);
}

/** The folder a path is in: /a/b → /a, /a → /, / → /. */
export function parentOf(path: string): string {
  const trimmed = path.replace(/\/+$/, '');
  const at = trimmed.lastIndexOf('/');
  return at <= 0 ? '/' : trimmed.slice(0, at);
}

/** A name in a folder: /a and b → /a/b, / and b → /b. */
export function joinPath(folder: string, name: string): string {
  return folder === '/' ? `/${name}` : `${folder.replace(/\/+$/, '')}/${name}`;
}

/** An entry's second line: a file's size and when it changed; a folder's change only. */
export function entryDetail(entry: FolderEntry): string {
  const when = new Date(entry.modified);
  const date = Number.isNaN(when.getTime())
    ? ''
    : when.toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' });
  return [entry.size === null ? '' : sizeText(entry.size), date].filter(Boolean).join(' · ');
}

/** What the status line says about a listing. */
export function listingText(listing: Listing): string {
  const folders = listing.entries.filter((e) => e.folder).length;
  const files = listing.entries.length - folders;
  if (listing.entries.length === 0) return 'This folder is empty.';
  const counts = [
    folders ? (folders === 1 ? '1 folder' : `${folders} folders`) : '',
    files ? (files === 1 ? '1 file' : `${files} files`) : '',
  ]
    .filter(Boolean)
    .join(', ');
  return listing.truncated
    ? `The first ${listing.entries.length} entries: ${counts}.`
    : `${counts}.`;
}

export class FilesPanel {
  readonly #root: HTMLElement;
  readonly #system: HTMLElement;
  readonly #body: HTMLElement;
  readonly #status: HTMLElement;
  #current: FilesSystem | undefined;
  #recent: string[] = [];
  #ibmUser = '';
  /** The folder listed, once one is. */
  #folder = '';
  /** Where the recent downloads show, under the folder's entries. */
  readonly #recentBox = h('div', { class: 'files-recent' });
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

  /** Shows the panel for a system: its sign-on, or its folders. */
  async show(system: FilesSystem | undefined, force = false): Promise<void> {
    if (!force && system?.name === this.#current?.name && this.#body.childElementCount > 0) return;
    if (system?.name !== this.#current?.name) {
      this.#recent = [];
      this.#folder = '';
    }
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
      if (state.signedOn && state.ibmUser) await this.#start(state.ibmUser, generation);
      else this.#signOnForm(state, system.ibmUser ?? state.suggestedUser ?? '');
    } catch (error) {
      if (generation === this.#generation) this.#fail(error);
    }
  }

  /** Puts the focus in the panel: the first field, entry or button. */
  focus(): void {
    this.#root
      .querySelector<HTMLElement>('.files-entry, input, button:not(.printer-close)')
      ?.focus();
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
        text: 'Sign on with your IBM i user and password to browse and download files in the IFS. You see only what your profile may see.',
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
      await this.#start(reply.ibmUser, generation);
    } catch (error) {
      if (generation !== this.#generation) return;
      this.#say((error as Error).message, true);
      this.#root.querySelector<HTMLInputElement>('#files-password')?.focus();
    }
  }

  /** Signed on: the folder listed before, else the user's home folder, else the root. */
  async #start(ibmUser: string, generation: number): Promise<void> {
    this.#ibmUser = ibmUser;
    if (this.#folder && (await this.#list(this.#folder, generation, true))) return;
    if (generation !== this.#generation) return;
    if (await this.#list(`/home/${ibmUser}`, generation, true)) return;
    if (generation !== this.#generation) return;
    await this.#list('/', generation);
  }

  /**
   * Lists a folder. With `quiet`, a refusal says nothing and returns false, so another folder
   * can be tried; otherwise it is said and the folder shown stays.
   */
  async #list(folder: string, generation = this.#generation, quiet = false): Promise<boolean> {
    if (!quiet) this.#say(`Listing ${folder}…`);
    let listing: Listing;
    try {
      listing = await api<Listing>('GET', this.#path(`/list?path=${encodeURIComponent(folder)}`));
    } catch (error) {
      if (generation !== this.#generation) return false;
      if (error instanceof ApiError && error.code === 'signon-needed') {
        this.#fail(error);
        return true;
      }
      if (!quiet) this.#say((error as Error).message, true);
      return false;
    }
    if (generation !== this.#generation) return true;
    this.#folder = listing.path;
    this.#browser(listing);
    this.#say(listingText(listing));
    return true;
  }

  /** The signed-on view: where we are, the folder's entries, and this panel's recent downloads. */
  #browser(listing: Listing): void {
    const pathBox = h('input', {
      class: 'printer-input files-path',
      id: 'files-path',
      type: 'text',
      autocomplete: 'off',
      spellcheck: 'false',
      'aria-label': 'Path of a folder or file',
    });
    pathBox.value = listing.path;
    const go = h('button', { type: 'submit', class: 'printer-btn', text: 'Go' });
    go.title = 'Open the folder, or download the file (Enter)';
    const where = h('form', { class: 'files-where', 'aria-label': 'Go to a path' }, pathBox, go);
    where.addEventListener('submit', (event) => {
      event.preventDefault();
      go.disabled = true;
      void this.#go(pathBox.value.trim()).finally(() => {
        go.disabled = false;
      });
    });

    const up = button('↑ Up', () => void this.#list(parentOf(listing.path)), 'printer-btn');
    up.title = 'The folder this one is in (Backspace)';
    up.disabled = listing.path === '/';
    const refresh = button('Refresh', () => void this.#list(listing.path), 'printer-btn');
    const signOff = button('Sign off', () => void this.#signOff(), 'printer-btn');

    const rows = listing.entries.map((entry) => this.#row(listing.path, entry));
    const list = h(
      'ul',
      { class: 'printer-files files-entries', 'aria-label': `Contents of ${listing.path}` },
      ...rows,
    );
    list.addEventListener('keydown', (event) => {
      if (event.key === 'Backspace' && listing.path !== '/') {
        event.preventDefault();
        void this.#list(parentOf(listing.path));
        return;
      }
      const at = rows.findIndex((row) => row.contains(document.activeElement));
      const to = nextRow(at, rows.length, event.key);
      if (to === undefined) return;
      event.preventDefault();
      rows[to]?.querySelector<HTMLElement>('.files-entry')?.focus();
    });

    this.#body.replaceChildren(
      h(
        'div',
        { class: 'printer-bar' },
        h('span', { class: 'printer-user', text: `Signed on as ${this.#ibmUser}` }),
        signOff,
      ),
      where,
      h('div', { class: 'files-tools' }, up, refresh),
      listing.entries.length
        ? list
        : h('p', { class: 'printer-empty', text: 'This folder is empty.' }),
      this.#recentBox,
    );
    this.#showRecent();
    (rows[0]?.querySelector<HTMLElement>('.files-entry') ?? pathBox).focus();
  }

  #row(folder: string, entry: FolderEntry): HTMLElement {
    const path = joinPath(folder, entry.name);
    const open = h(
      'button',
      {
        type: 'button',
        class: `printer-file files-entry${entry.folder ? ' files-folder' : ''}`,
        title: entry.folder ? 'Open the folder (Enter)' : 'Download (Enter)',
      },
      h(
        'span',
        { class: 'printer-file-name' },
        h('strong', { text: entry.folder ? `${entry.name}/` : entry.name }),
      ),
      h('span', { class: 'printer-file-detail', text: entryDetail(entry) }),
    );
    open.addEventListener('click', () => {
      if (entry.folder) void this.#list(path);
      else void this.#download(path);
    });
    return h('li', {}, open);
  }

  /** The path box: a file downloads, a folder opens. */
  async #go(path: string): Promise<void> {
    const problem = pathProblem(path);
    if (problem) {
      this.#say(problem, true);
      return;
    }
    const generation = this.#generation;
    try {
      await this.#fetch(path, generation);
    } catch (error) {
      if (generation !== this.#generation) return;
      if (error instanceof ApiError && error.code === 'folder') await this.#list(path);
      else this.#fail(error);
    }
  }

  /** Downloads a file, saying why not when it can't. */
  async #download(path: string): Promise<void> {
    const generation = this.#generation;
    try {
      await this.#fetch(path, generation);
    } catch (error) {
      if (generation === this.#generation) this.#fail(error);
    }
  }

  /** Checks a file, then has the browser save it. Throws what the check refused. */
  async #fetch(path: string, generation: number): Promise<void> {
    this.#say(`Checking ${path}…`);
    const info = await api<FileInfo>('GET', this.#path(`/info?path=${encodeURIComponent(path)}`));
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
    this.#showRecent();
    this.#say(`Downloading ${info.name} (${sizeText(info.size)}).`);
  }

  /** This panel's recent downloads, to download again. */
  #showRecent(): void {
    this.#recentBox.replaceChildren(
      ...(this.#recent.length
        ? [
            h('p', { class: 'printer-note', text: 'Downloaded' }),
            h(
              'ul',
              { class: 'printer-files', 'aria-label': 'Recent downloads' },
              ...this.#recent.map((path) => {
                const again = button(
                  path,
                  () => void this.#download(path),
                  'printer-file files-again',
                );
                again.title = 'Download again';
                return h('li', {}, again);
              }),
            ),
          ]
        : []),
    );
  }

  async #signOff(): Promise<void> {
    try {
      await api('DELETE', this.#path('/signon'));
    } catch (error) {
      this.#fail(error);
      return;
    }
    this.#folder = '';
    await this.show(this.#current, true);
    this.#say('Signed off.');
  }
}
