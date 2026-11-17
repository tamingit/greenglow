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

/** One file (or empty folder) to upload, and how it went. */
export interface UploadItem {
  /** Null for an empty folder, which is made rather than sent. */
  file: File | null;
  /** Where it goes, from the root of the IFS. */
  path: string;
  state: 'waiting' | 'sending' | 'done' | 'exists' | 'failed' | 'stopped';
  note: string;
}

/** What to upload: each file at its relative path (a folder's own name first) in `folder`. */
export function uploadItems(
  folder: string,
  files: readonly { file: File | null; relative: string }[],
): UploadItem[] {
  return files.map(({ file, relative }) => ({
    file,
    path: joinPath(folder, relative.replace(/^\/+/, '')),
    state: 'waiting',
    note: '',
  }));
}

/** Why a name can't be used for a new folder or a rename, or undefined when it can. */
export function nameProblem(name: string): string | undefined {
  if (name === '') return 'Type a name.';
  if (name.includes('/')) return 'A name can’t hold a /.';
  if (name === '.' || name === '..') return 'A name can’t be . or ..';
  return undefined;
}

/** Whether the panel leaves a path alone: / and the folders directly in it (QSYS.LIB, home ...). */
export function protectedPath(path: string): boolean {
  return path.split('/').filter(Boolean).length <= 1;
}

/** What the status line says when an upload ends. */
export function uploadSummary(items: readonly UploadItem[]): string {
  const count = (state: UploadItem['state']) => items.filter((i) => i.state === state).length;
  const files = (n: number) => (n === 1 ? '1 file' : `${n} files`);
  const parts = [
    count('done') ? `Uploaded ${files(count('done'))}.` : '',
    count('exists') ? `${files(count('exists'))} already there, not replaced.` : '',
    count('failed') ? `${files(count('failed'))} failed.` : '',
    count('stopped') || count('waiting') ? 'Stopped.' : '',
  ];
  return parts.filter(Boolean).join(' ') || 'Nothing to upload.';
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
  /** The upload under way or last done, shown under the folder's entries. */
  readonly #uploadBox = h('div', { class: 'files-uploads' });
  /** A question in the panel: a name to type, or whether to delete. */
  readonly #askBox = h('div', { class: 'files-ask' });
  #uploads: UploadItem[] = [];
  #uploading = false;
  #stop: (() => void) | undefined;
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
    const newFolder = button('New folder…', () => this.#newFolder(listing.path), 'printer-btn');
    newFolder.title = `Make a folder in ${listing.path}`;
    this.#askBox.replaceChildren();
    const signOff = button('Sign off', () => void this.#signOff(), 'printer-btn');

    const rows = listing.entries.map((entry) => this.#row(listing.path, entry));
    const list = h(
      'ul',
      { class: 'printer-files files-entries', 'aria-label': `Contents of ${listing.path}` },
      ...rows,
    );
    list.addEventListener('keydown', (event) => {
      const entry = listing.entries[rows.findIndex((row) => row.contains(document.activeElement))];
      if ((event.key === 'F2' || event.key === 'Delete') && entry) {
        if (protectedPath(joinPath(listing.path, entry.name))) return;
        event.preventDefault();
        if (event.key === 'F2') this.#rename(listing.path, entry);
        else this.#delete(listing.path, entry);
        return;
      }
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
      h('div', { class: 'files-tools' }, up, refresh, newFolder),
      this.#askBox,
      this.#uploadTools(listing.path),
      this.#dropZone(
        listing.path,
        listing.entries.length
          ? list
          : h('p', { class: 'printer-empty', text: 'This folder is empty. Drop files here.' }),
      ),
      this.#uploadBox,
      this.#recentBox,
    );
    this.#showRecent();
    this.#showUploads();
    (rows[0]?.querySelector<HTMLElement>('.files-entry') ?? pathBox).focus();
  }

  /** Upload files, Upload folder and Replace: files chosen go into `folder`. */
  #uploadTools(folder: string): HTMLElement {
    const files = h('input', { type: 'file', multiple: true, hidden: true });
    const tree = h('input', { type: 'file', multiple: true, hidden: true, webkitdirectory: true });
    const pick = (input: HTMLInputElement) => () => {
      if (this.#uploading) return;
      input.value = '';
      input.click();
    };
    const chosen = (input: HTMLInputElement) => () => {
      const list = [...(input.files ?? [])].map((file) => ({
        file,
        relative: file.webkitRelativePath || file.name,
      }));
      if (list.length) void this.#upload(uploadItems(folder, list));
    };
    files.addEventListener('change', chosen(files));
    tree.addEventListener('change', chosen(tree));
    const uploadFiles = button('Upload files…', pick(files), 'printer-btn');
    uploadFiles.title = `Send files into ${folder}`;
    const uploadFolder = button('Upload folder…', pick(tree), 'printer-btn');
    uploadFolder.title = `Send a folder, with everything in it, into ${folder}`;
    const replace = h('input', { type: 'checkbox', id: 'files-replace' });
    replace.checked = this.#replace;
    replace.addEventListener('change', () => (this.#replace = replace.checked));
    return h(
      'div',
      { class: 'files-upload-tools' },
      h('div', { class: 'files-tools' }, uploadFiles, uploadFolder, files, tree),
      h('label', { class: 'files-check' }, replace, ' Replace files that are already there'),
    );
  }

  #replace = false;

  /** The folder's entries, taking files and folders dropped on them. */
  #dropZone(folder: string, inner: HTMLElement): HTMLElement {
    const zone = h('div', { class: 'files-drop' }, inner);
    zone.addEventListener('dragover', (event) => {
      if (!event.dataTransfer?.types.includes('Files') || this.#uploading) return;
      event.preventDefault();
      event.dataTransfer.dropEffect = 'copy';
      zone.dataset.over = '';
    });
    zone.addEventListener('dragleave', () => delete zone.dataset.over);
    zone.addEventListener('drop', (event) => {
      delete zone.dataset.over;
      if (!event.dataTransfer || this.#uploading) return;
      event.preventDefault();
      const entries = [...event.dataTransfer.items]
        .map((item) => item.webkitGetAsEntry())
        .filter((entry): entry is FileSystemEntry => entry !== null);
      void collect(entries).then((list) => {
        if (list.length) void this.#upload(uploadItems(folder, list));
      });
    });
    return zone;
  }

  /** Sends each item in turn: a folder is made, a file is sent with its progress shown. */
  async #upload(items: UploadItem[], replace = this.#replace): Promise<void> {
    if (this.#uploading) return;
    this.#uploading = true;
    this.#uploads = items;
    let stopped = false;
    let abort: (() => void) | undefined;
    this.#stop = () => {
      stopped = true;
      abort?.();
    };
    this.#showUploads();
    for (const item of items) {
      if (stopped) {
        item.state = 'stopped';
        continue;
      }
      item.state = 'sending';
      this.#showUploads();
      try {
        if (item.file === null) {
          await api('POST', this.#path(`/folder?path=${encodeURIComponent(item.path)}`));
        } else {
          const file = item.file;
          const sending = put(
            this.#path(
              `/upload?path=${encodeURIComponent(item.path)}&replace=${replace ? 'true' : 'false'}`,
            ),
            file,
            (sent) => {
              item.note = file.size ? `${Math.floor((sent / file.size) * 100)}%` : '';
              this.#showUploads();
            },
          );
          abort = sending.abort;
          await sending.done;
        }
        item.state = 'done';
        item.note = item.file ? sizeText(item.file.size) : 'folder';
      } catch (error) {
        if (stopped) item.state = 'stopped';
        else if (error instanceof ApiError && error.code === 'exists') item.state = 'exists';
        else item.state = 'failed';
        item.note = item.state === 'stopped' ? '' : (error as Error).message;
        if (error instanceof ApiError && error.code === 'signon-needed') stopped = true;
      }
      this.#showUploads();
    }
    this.#uploading = false;
    this.#stop = undefined;
    this.#showUploads();
    this.#say(
      uploadSummary(items),
      items.some((i) => i.state === 'failed'),
    );
    // The folder shown now gets the new files.
    if (this.#folder) {
      const said = this.#status.textContent;
      const error = this.#status.dataset.error === 'true';
      await this.#list(this.#folder, this.#generation, true);
      this.#say(said ?? '', error);
    }
  }

  /** The upload's rows: each path, how it went, Stop while it runs, Replace for those already there. */
  #showUploads(): void {
    if (!this.#uploads.length) {
      this.#uploadBox.replaceChildren();
      return;
    }
    const waiting = this.#uploads.filter((i) => i.state !== 'done').length;
    const stop = this.#uploading ? button('Stop', () => this.#stop?.(), 'printer-btn') : undefined;
    const there = this.#uploads.filter((i) => i.state === 'exists');
    const replace =
      !this.#uploading && there.length
        ? button(
            there.length === 1 ? 'Replace it' : `Replace the ${there.length} already there`,
            () =>
              void this.#upload(
                there.map((i) => ({ ...i, state: 'waiting', note: '' })),
                true,
              ),
            'printer-btn',
          )
        : undefined;
    const clear = !this.#uploading
      ? button(
          'Clear',
          () => {
            this.#uploads = [];
            this.#showUploads();
          },
          'printer-btn',
        )
      : undefined;
    const words: Record<UploadItem['state'], string> = {
      waiting: 'Waiting',
      sending: 'Sending',
      done: 'Done',
      exists: 'Already there',
      failed: 'Failed',
      stopped: 'Stopped',
    };
    this.#uploadBox.replaceChildren(
      h(
        'p',
        { class: 'printer-note' },
        this.#uploading ? `Uploading (${waiting} to go)` : 'Uploaded',
      ),
      h(
        'ul',
        { class: 'printer-files files-upload-list', 'aria-label': 'Uploads' },
        ...this.#uploads.map((item) =>
          h(
            'li',
            { class: 'files-upload', 'data-state': item.state },
            h('span', { class: 'files-upload-path', text: item.path }),
            h('span', {
              class: 'printer-file-detail',
              text: [words[item.state], item.note].filter(Boolean).join(' · '),
            }),
          ),
        ),
      ),
      h('div', { class: 'files-tools' }, ...[stop, replace, clear].filter((b) => b !== undefined)),
    );
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
    if (protectedPath(path)) return h('li', { class: 'files-row' }, open);
    const rename = button('Rename', () => this.#rename(folder, entry), 'printer-btn files-action');
    rename.title = `Rename ${entry.name} (F2)`;
    const remove = button('Delete', () => this.#delete(folder, entry), 'printer-btn files-action');
    remove.title = `Delete ${entry.name} (Delete)`;
    return h(
      'li',
      { class: 'files-row' },
      open,
      h('span', { class: 'files-actions' }, rename, remove),
    );
  }

  /** Asks for a name in the panel; Escape or Cancel puts the question away. */
  #askName(
    label: string,
    value: string,
    submit: string,
    act: (name: string) => Promise<void>,
  ): void {
    const box = h('input', {
      class: 'printer-input',
      id: 'files-name',
      type: 'text',
      autocomplete: 'off',
      spellcheck: 'false',
    });
    box.value = value;
    const ok = h('button', { type: 'submit', class: 'printer-btn printer-primary', text: submit });
    const cancel = button('Cancel', () => this.#putAway(), 'printer-btn');
    const form = h(
      'form',
      { class: 'files-ask-form', 'aria-label': label },
      h('label', { for: 'files-name', text: label }),
      box,
      h('div', { class: 'files-tools' }, ok, cancel),
    );
    form.addEventListener('submit', (event) => {
      event.preventDefault();
      const name = box.value.trim();
      const problem = nameProblem(name);
      if (problem) {
        this.#say(problem, true);
        box.focus();
        return;
      }
      ok.disabled = true;
      void act(name).finally(() => (ok.disabled = false));
    });
    this.#show(form);
    box.focus();
    box.select();
  }

  /** Asks whether to go ahead, saying what will happen. */
  #confirm(text: string, go: string, act: () => Promise<void>): void {
    const yes = button(
      go,
      () => {
        yes.disabled = true;
        void act().finally(() => (yes.disabled = false));
      },
      'printer-btn files-danger',
    );
    const no = button('Cancel', () => this.#putAway(), 'printer-btn');
    this.#show(
      h(
        'div',
        { class: 'files-ask-form', role: 'alertdialog', 'aria-label': text },
        h('p', { class: 'files-ask-text', text }),
        h('div', { class: 'files-tools' }, yes, no),
      ),
    );
    no.focus();
  }

  #show(question: HTMLElement): void {
    question.addEventListener('keydown', (event) => {
      if (event.key !== 'Escape') return;
      // Puts the question away, not the panel.
      event.preventDefault();
      event.stopPropagation();
      this.#putAway();
    });
    this.#askBox.replaceChildren(question);
  }

  #putAway(): void {
    this.#askBox.replaceChildren();
    this.#root.querySelector<HTMLElement>('.files-entry, #files-path')?.focus();
  }

  #newFolder(folder: string): void {
    this.#askName(`Name of the new folder in ${folder}`, '', 'Make', async (name) => {
      try {
        await api(
          'POST',
          this.#path(`/folder?strict=true&path=${encodeURIComponent(joinPath(folder, name))}`),
        );
      } catch (error) {
        this.#fail(error);
        return;
      }
      await this.#list(folder, this.#generation, true);
      this.#say(`Made ${name}/.`);
    });
  }

  #rename(folder: string, entry: FolderEntry): void {
    const shown = entry.folder ? `${entry.name}/` : entry.name;
    this.#askName(`New name for ${shown}`, entry.name, 'Rename', async (name) => {
      if (name === entry.name) {
        this.#putAway();
        return;
      }
      try {
        await api(
          'POST',
          this.#path(
            `/rename?path=${encodeURIComponent(joinPath(folder, entry.name))}&name=${encodeURIComponent(name)}`,
          ),
        );
      } catch (error) {
        this.#fail(error);
        return;
      }
      await this.#list(folder, this.#generation, true);
      this.#say(`Renamed ${shown} to ${name}${entry.folder ? '/' : ''}.`);
    });
  }

  /** Deletes a file, or an empty folder; a folder with things in it is asked about again. */
  #delete(folder: string, entry: FolderEntry): void {
    const path = joinPath(folder, entry.name);
    const shown = entry.folder ? `${entry.name}/` : entry.name;
    const remove = async (recursive: boolean): Promise<void> => {
      let deleted: number;
      try {
        ({ deleted } = await api<{ deleted: number }>(
          'DELETE',
          this.#path(
            `/entry?folder=${entry.folder}&recursive=${recursive}&path=${encodeURIComponent(path)}`,
          ),
        ));
      } catch (error) {
        if (!recursive && error instanceof ApiError && error.code === 'not-empty') {
          this.#confirm(
            `${shown} has files or folders in it. Delete it and everything in it? This can't be undone.`,
            'Delete everything',
            () => remove(true),
          );
          return;
        }
        this.#fail(error);
        return;
      }
      await this.#list(folder, this.#generation, true);
      this.#say(
        recursive
          ? `Deleted ${shown} and everything in it (${deleted} ${deleted === 1 ? 'entry' : 'entries'}).`
          : `Deleted ${shown}.`,
      );
    };
    this.#confirm(`Delete ${shown}? This can't be undone.`, 'Delete', () => remove(false));
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

/**
 * Sends a file as a request's body with PUT, telling its progress. XMLHttpRequest, because fetch
 * can't tell how much of an upload has gone. A refusal rejects with the server's ApiError.
 */
function put(
  url: string,
  file: File,
  progress: (sent: number) => void,
): { done: Promise<void>; abort: () => void } {
  const request = new XMLHttpRequest();
  const done = new Promise<void>((resolve, reject) => {
    request.open('PUT', url);
    request.setRequestHeader('content-type', 'application/octet-stream');
    request.upload.addEventListener('progress', (event) => progress(event.loaded));
    request.addEventListener('load', () => {
      if (request.status >= 200 && request.status < 300) {
        resolve();
        return;
      }
      let message = `Upload failed (${request.status})`;
      let code: string | undefined;
      try {
        const body = JSON.parse(request.responseText) as { message?: string; error?: string };
        message = body.message ?? message;
        code = body.error;
      } catch {
        // No JSON body: keep the status.
      }
      reject(new ApiError(request.status, message, code));
    });
    request.addEventListener('error', () =>
      reject(new Error('The upload could not reach the server.')),
    );
    request.addEventListener('abort', () => reject(new Error('Stopped.')));
    request.send(file);
  });
  return { done, abort: () => request.abort() };
}

/**
 * The files (and empty folders) in what was dropped, each with its path from the drop: a
 * dropped folder's own name comes first, as when a folder is chosen.
 */
async function collect(
  entries: FileSystemEntry[],
): Promise<{ file: File | null; relative: string }[]> {
  const found: { file: File | null; relative: string }[] = [];
  const walk = async (entry: FileSystemEntry, relative: string): Promise<void> => {
    if (entry.isFile) {
      const file = await new Promise<File>((resolve, reject) =>
        (entry as FileSystemFileEntry).file(resolve, reject),
      );
      found.push({ file, relative });
      return;
    }
    const reader = (entry as FileSystemDirectoryEntry).createReader();
    const children: FileSystemEntry[] = [];
    // readEntries answers in batches until it answers none.
    for (;;) {
      const batch = await new Promise<FileSystemEntry[]>((resolve, reject) =>
        reader.readEntries(resolve, reject),
      );
      if (!batch.length) break;
      children.push(...batch);
    }
    if (!children.length) found.push({ file: null, relative });
    for (const child of children) await walk(child, `${relative}/${child.name}`);
  };
  for (const entry of entries) await walk(entry, entry.name);
  return found;
}
