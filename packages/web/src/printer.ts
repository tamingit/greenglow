// The Printer output side panel: the user's own spooled files on the system of
// the tab showing, opened as PDFs in a new browser tab to read, save or print. The user signs on
// to the IBM i's print server here with their IBM i user and password; the password goes to the
// server once and is cleared from the form at once, never kept in the page or in storage.

import { ApiError, api } from './admin/api.ts';
import { button, h } from './admin/dom.ts';
import './printer.css';

/** A spooled file as /api/printer/{connection}/files lists it. */
export interface SpooledFileInfo {
  id: string;
  name: string;
  number: number;
  job: string;
  jobSystem: string;
  created: string | null;
  status: string;
  pages: number;
  userData: string;
  type: string;
  outputQueue: string;
  formType: string;
  canOpen: boolean;
}

interface PrinterState {
  signedOn: boolean;
  ibmUser: string | null;
  suggestedUser: string | null;
}

/** The system the panel is for: the one of the tab showing. */
export interface PrinterSystem {
  name: string;
  label: string;
  /** The IBM i user the tab's own session is signed on as, when it is. */
  ibmUser?: string;
}

/**
 * What the panel shows for a tab: the print session's list only when it is the tab's system and,
 * when the tab's session is signed on, the same IBM i user; otherwise the sign-on form, for
 * that user. The server keeps one print session per GreenGlow user and system.
 */
export function printerView(
  system: PrinterSystem,
  signedOnAs: string | null,
): { list: true } | { list: false; user: string | undefined; other: string | undefined } {
  if (signedOnAs !== null && (!system.ibmUser || system.ibmUser === signedOnAs))
    return { list: true };
  return { list: false, user: system.ibmUser, other: signedOnAs ?? undefined };
}

/** When a spooled file was made, short: the time today, else the date and time. */
export function createdText(created: string | null, now = new Date()): string {
  if (!created) return '';
  const when = new Date(created);
  if (Number.isNaN(when.getTime())) return '';
  const today = when.toDateString() === now.toDateString();
  return today
    ? when.toLocaleTimeString(undefined, { hour: 'numeric', minute: '2-digit' })
    : when.toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' });
}

/** The second and third lines of a file's row: when and how big, then where it came from. */
export function fileDetail(file: SpooledFileInfo, now = new Date()): [string, string] {
  const when = [
    createdText(file.created, now),
    file.pages === 1 ? '1 page' : `${file.pages} pages`,
    file.status,
  ];
  const where = [`job ${file.job}`, `file ${file.number}`, file.outputQueue];
  return [when.filter(Boolean).join(' · '), where.filter(Boolean).join(' · ')];
}

/** What the row says about a file that can't be opened here. */
export function cannotOpenText(file: SpooledFileInfo): string {
  return `${file.type} can't be shown here: open it in ACS.`;
}

/** The row to go to from `index` for a navigation key, or undefined for any other key. */
export function nextRow(index: number, count: number, key: string): number | undefined {
  if (count === 0) return undefined;
  switch (key) {
    case 'ArrowDown':
      return Math.min(count - 1, index + 1);
    case 'ArrowUp':
      return Math.max(0, index - 1);
    case 'Home':
      return 0;
    case 'End':
      return count - 1;
    case 'PageDown':
      return Math.min(count - 1, index + 10);
    case 'PageUp':
      return Math.max(0, index - 10);
    default:
      return undefined;
  }
}

export class PrinterPanel {
  readonly #root: HTMLElement;
  readonly #system: HTMLElement;
  readonly #body: HTMLElement;
  readonly #status: HTMLElement;
  #current: PrinterSystem | undefined;
  /** Counts what the panel shows, so a late answer for an earlier system is dropped. */
  #generation = 0;

  constructor(root: HTMLElement, closed: () => void) {
    this.#root = root;
    this.#system = h('p', { class: 'printer-system' });
    this.#body = h('div', { class: 'printer-body' });
    this.#status = h('p', { class: 'printer-status', role: 'status' });
    const close = button('×', closed, 'printer-close');
    close.setAttribute('aria-label', 'Close printer output panel');
    close.title = 'Close (Alt+P)';
    root.replaceChildren(
      h(
        'div',
        { class: 'printer-head' },
        h('h2', { id: 'printer-title', text: 'Printer output' }),
        close,
      ),
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

  /** Shows the panel for a system: its sign-on form, or the user's spooled files there. */
  async show(system: PrinterSystem | undefined, force = false): Promise<void> {
    if (
      !force &&
      system?.name === this.#current?.name &&
      system?.ibmUser === this.#current?.ibmUser &&
      this.#body.childElementCount > 0
    )
      return;
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
      const state = await api<PrinterState>(
        'GET',
        `/api/printer/${encodeURIComponent(system.name)}`,
      );
      if (generation !== this.#generation) return;
      const view = printerView(system, state.signedOn ? state.ibmUser : null);
      if (view.list) await this.#list(generation);
      else if (view.other && view.user)
        this.#signOnForm(
          view.user,
          `Printer output on ${system.label} is signed on as ${view.other}. Sign on as ${view.user} for this session's spooled files.`,
          false,
        );
      else this.#signOnForm(view.user ?? state.suggestedUser ?? '');
    } catch (error) {
      if (generation === this.#generation) this.#fail(error);
    }
  }

  /** Puts the focus in the panel: the first field or file. */
  focus(): void {
    this.#root
      .querySelector<HTMLElement>('input, .printer-file, button:not(.printer-close)')
      ?.focus();
  }

  #say(text: string, error = false): void {
    this.#status.textContent = text;
    this.#status.dataset.error = String(error);
  }

  #fail(error: unknown): void {
    if (error instanceof ApiError && error.code === 'signon-needed') {
      this.#signOnForm('', error.message);
      return;
    }
    this.#say((error as Error).message, true);
  }

  #path(rest = ''): string {
    return `/api/printer/${encodeURIComponent(this.#current?.name ?? '')}${rest}`;
  }

  #signOnForm(user: string, message = '', error = message !== ''): void {
    const userBox = h('input', {
      class: 'printer-input',
      id: 'printer-user',
      type: 'text',
      autocomplete: 'off',
      spellcheck: 'false',
      maxlength: '10',
    });
    userBox.value = user;
    const passwordBox = h('input', {
      class: 'printer-input',
      id: 'printer-password',
      type: 'password',
      autocomplete: 'off',
    });
    const submit = h('button', {
      type: 'submit',
      class: 'printer-btn printer-primary',
      text: 'Sign on',
    });
    const form = h(
      'form',
      { class: 'printer-form', 'aria-label': 'Sign on to printer output' },
      h('p', {
        class: 'printer-intro',
        // The demo's fake IBM i has one placeholder print user.
        text:
          'demo' in document.documentElement.dataset
            ? 'Demo: on the fake IBM i, sign on as PRTUSER with the password Secret12.'
            : 'Sign on with your IBM i user and password to see your spooled files.',
      }),
      h('label', { for: 'printer-user', text: 'IBM i user' }),
      userBox,
      h('label', { for: 'printer-password', text: 'Password' }),
      passwordBox,
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
      void this.#signOn(userBox.value.trim(), password).finally(() => {
        submit.disabled = false;
      });
    });
    this.#body.replaceChildren(form);
    this.#say(message, error);
    (user ? passwordBox : userBox).focus();
  }

  async #signOn(user: string, password: string): Promise<void> {
    const generation = this.#generation;
    this.#say('Signing on…');
    try {
      await api('POST', this.#path('/signon'), { user, password });
      if (generation !== this.#generation) return;
      await this.#list(generation);
    } catch (error) {
      if (generation !== this.#generation) return;
      this.#say((error as Error).message, true);
      this.#root.querySelector<HTMLInputElement>('#printer-password')?.focus();
    }
  }

  async #list(generation: number): Promise<void> {
    this.#say('Loading…');
    let files: SpooledFileInfo[];
    let ibmUser: string;
    try {
      ({ files, ibmUser } = await api<{ files: SpooledFileInfo[]; ibmUser: string }>(
        'GET',
        this.#path('/files'),
      ));
    } catch (error) {
      if (generation === this.#generation) this.#fail(error);
      return;
    }
    if (generation !== this.#generation) return;

    const refresh = button('Refresh', () => void this.#list(this.#generation), 'printer-btn');
    refresh.title = 'List your spooled files again';
    const signOff = button('Sign off', () => void this.#signOff(), 'printer-btn');
    const list = h('ul', { class: 'printer-files', 'aria-label': 'Your spooled files' });
    const rows = files.map((file) => this.#row(file));
    list.append(...rows);
    list.addEventListener('keydown', (event) => {
      const at = rows.findIndex((row) => row.contains(document.activeElement));
      const to = nextRow(at, rows.length, event.key);
      if (to === undefined) return;
      event.preventDefault();
      rows[to]?.querySelector<HTMLElement>('.printer-file')?.focus();
    });
    this.#body.replaceChildren(
      h(
        'div',
        { class: 'printer-bar' },
        h('span', { class: 'printer-user', text: `Signed on as ${ibmUser}` }),
        refresh,
        signOff,
      ),
      files.length === 0
        ? h('p', { class: 'printer-empty', text: 'You have no spooled files.' })
        : list,
    );
    this.#say(
      files.length === 0
        ? ''
        : files.length === 1
          ? '1 spooled file.'
          : `${files.length} spooled files.`,
    );
  }

  #row(file: SpooledFileInfo): HTMLElement {
    const open = h(
      'button',
      {
        type: 'button',
        class: 'printer-file',
        'aria-disabled': file.canOpen ? false : 'true',
        title: file.canOpen ? 'Open as a PDF in a new tab (Enter)' : cannotOpenText(file),
      },
      h(
        'span',
        { class: 'printer-file-name' },
        h('strong', { text: file.name }),
        file.userData ? h('span', { class: 'printer-file-data', text: file.userData }) : null,
        file.canOpen ? null : h('span', { class: 'printer-file-type', text: file.type }),
      ),
      ...fileDetail(file).map((line) => h('span', { class: 'printer-file-detail', text: line })),
    );
    open.addEventListener('click', () => this.#open(file));
    return h('li', {}, open);
  }

  /** Opens a spooled file as a PDF in a new browser tab: printing is the browser's. */
  #open(file: SpooledFileInfo): void {
    if (!file.canOpen) {
      this.#say(cannotOpenText(file), true);
      return;
    }
    // Opened now, while the click still counts, so the browser doesn't block it.
    const tab = window.open('', '_blank');
    if (!tab) {
      this.#say(
        'The browser blocked the new tab. Allow pop-ups for GreenGlow, then try again.',
        true,
      );
      return;
    }
    tab.opener = null;
    const generation = this.#generation;
    this.#say(`Opening ${file.name}…`);
    // The print session may have ended (idle, or the IBM i dropped it): check before opening.
    void api<PrinterState>('GET', this.#path())
      .then((state) => {
        if (!state.signedOn) {
          tab.close();
          if (generation === this.#generation)
            this.#signOnForm(
              state.suggestedUser ?? '',
              'Your printer output sign-on ended. Sign on again.',
            );
          return;
        }
        tab.location.href = this.#path(`/files/${encodeURIComponent(file.id)}`);
        if (generation === this.#generation) this.#say(`Opened ${file.name} in a new tab.`);
      })
      .catch((error: unknown) => {
        tab.close();
        if (generation === this.#generation) this.#fail(error);
      });
  }

  async #signOff(): Promise<void> {
    try {
      await api('DELETE', this.#path('/signon'));
    } catch (error) {
      this.#fail(error);
      return;
    }
    this.#signOnForm(this.#current?.ibmUser ?? '', 'Signed off.', false);
  }
}
