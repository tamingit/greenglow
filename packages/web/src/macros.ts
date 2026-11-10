// The Macros side panel: the user's own macros, to make, share, rename and delete, and the ones
// shared with them. A macro is Host-on-Demand macro XML on the server (/api/macros); playing,
// recording and key combinations come next.

import { ApiError, api } from './admin/api.ts';
import { button, h } from './admin/dom.ts';
import { holdsPassword, parseHodMacro } from './hodmacro.ts';
import { openMacroShareDialog } from './macroshare.ts';
import './macros.css';

interface MacroInfo {
  id: string;
  name: string;
  description: string;
  owner: string;
  mine: boolean;
  /** For the owner: how many people and groups it is shared with. */
  sharedWith: number;
  keys: string | null;
}

/** Recording a macro on the session showing (main.ts and recorder.ts do the work). */
export interface RecordingActions {
  /** Starts recording; returns why it can't, if it can't. */
  start(name: string): string | undefined;
  /** Stops and saves the macro. */
  stop(): Promise<void>;
  cancel(): void;
  /** Plays a macro on the session showing. */
  play(macro: { id: string; name: string }): void;
  /** The macros the user may play, with their key combinations, each time they are listed. */
  listed(macros: readonly { id: string; name: string; keys: string | null }[]): void;
}

export class MacrosPanel {
  readonly #recording: RecordingActions;
  readonly #recordBar: HTMLElement;
  readonly #recordText: HTMLElement;
  readonly #submit: HTMLButtonElement;
  /** What the name box is for: a new empty macro, or one to record. */
  #mode: 'add' | 'record' = 'add';
  readonly #mine: HTMLElement;
  readonly #shared: HTMLElement;
  readonly #sharedSection: HTMLElement;
  readonly #status: HTMLElement;
  readonly #form: HTMLFormElement;
  readonly #name: HTMLInputElement;

  constructor(root: HTMLElement, closed: () => void, recording: RecordingActions) {
    this.#recording = recording;
    this.#mine = h('ul', { class: 'macros-list', 'aria-label': 'Your macros' });
    this.#shared = h('ul', { class: 'macros-list', 'aria-label': 'Macros shared with you' });
    this.#status = h('p', { class: 'macros-status', role: 'status' });
    this.#name = h('input', {
      class: 'macros-input',
      type: 'text',
      maxlength: '60',
      placeholder: 'Name',
      'aria-label': 'New macro name',
    });
    this.#submit = h('button', { type: 'submit', class: 'macros-btn', text: 'Add' });
    this.#form = h('form', { class: 'macros-new', hidden: true }, this.#name, this.#submit);
    this.#form.addEventListener('submit', (event) => {
      event.preventDefault();
      if (this.#mode === 'add') {
        void this.#create(this.#name.value);
        return;
      }
      const why = this.#recording.start(this.#name.value.trim());
      if (why) this.#status.textContent = why;
      else this.#form.hidden = true;
    });
    const add = button('+', () => this.#startNew('add'), 'macros-add');
    add.setAttribute('aria-label', 'New macro');
    add.title = 'New macro';
    const record = button('●', () => this.#startNew('record'), 'macros-add macros-record');
    record.setAttribute('aria-label', 'Record a macro');
    record.title = 'Record a macro on the session showing';
    this.#recordText = h('span', { class: 'macros-recording-text' });
    const stop = button('Stop', () => void this.#recording.stop(), 'macros-btn');
    stop.setAttribute('aria-label', 'Stop recording and save');
    const cancel = button('Cancel', () => this.#recording.cancel(), 'macros-btn');
    cancel.setAttribute('aria-label', 'Cancel recording');
    this.#recordBar = h(
      'div',
      { class: 'macros-recording', role: 'status', hidden: true },
      this.#recordText,
      stop,
      cancel,
    );
    this.#sharedSection = h(
      'section',
      { class: 'macros-section', 'aria-labelledby': 'macros-shared-title', hidden: true },
      h('h3', { id: 'macros-shared-title', text: 'Shared with you' }),
      this.#shared,
    );
    // Import: a .mac file as ACS saves it.
    const file = h('input', { type: 'file', accept: '.mac,.xml,text/xml', hidden: true });
    file.addEventListener('change', () => {
      const chosen = file.files?.[0];
      file.value = '';
      if (chosen) void this.#import(chosen);
    });
    const load = button('⇩', () => file.click(), 'macros-add');
    load.setAttribute('aria-label', 'Import a macro (.mac)');
    load.title = 'Import a macro saved by ACS (.mac)';
    root.replaceChildren(
      h(
        'div',
        { class: 'macros-head' },
        h('h2', { id: 'macros-title', text: 'Macros' }),
        h('span', { class: 'macros-head-buttons' }, record, load, add, file),
      ),
      this.#recordBar,
      this.#form,
      h(
        'section',
        { class: 'macros-section', 'aria-labelledby': 'macros-mine-title' },
        h('h3', { id: 'macros-mine-title', text: 'Yours' }),
        this.#mine,
      ),
      this.#sharedSection,
      this.#status,
    );
    root.addEventListener('keydown', (event) => {
      if (event.key !== 'Escape') return;
      event.preventDefault();
      closed();
    });
  }

  /** Lists the macros again; called when the panel is pulled out. */
  async refresh(): Promise<void> {
    let macros: MacroInfo[];
    try {
      macros = (await api<{ macros: MacroInfo[] }>('GET', '/api/macros')).macros;
    } catch (error) {
      this.#status.textContent = (error as Error).message;
      return;
    }
    this.#recording.listed(macros);
    const mine = macros.filter((m) => m.mine);
    const shared = macros.filter((m) => !m.mine);
    this.#mine.replaceChildren(
      ...(mine.length === 0
        ? [h('li', { class: 'macros-empty', text: 'None yet. + makes one.' })]
        : mine.map((m) => this.#row(m))),
    );
    this.#shared.replaceChildren(...shared.map((m) => this.#row(m)));
    this.#sharedSection.hidden = shared.length === 0;
  }

  #row(macro: MacroInfo): HTMLElement {
    const name = h(
      'span',
      { class: 'macros-name' },
      h('span', { text: macro.name }),
      h('span', {
        class: 'macros-detail',
        text: [
          macro.mine ? '' : macro.owner,
          macro.keys ?? '',
          macro.description,
          macro.mine && macro.sharedWith > 0 ? `shared with ${macro.sharedWith}` : '',
        ]
          .filter(Boolean)
          .join(' · '),
      }),
    );
    // The buttons stay together: beside the name, or under a long one.
    const actions = h('span', { class: 'macros-actions' });
    const row = h('li', { class: 'macros-row', 'data-macro': String(macro.id) }, name, actions);
    const play = button('▶', () => this.#recording.play(macro), 'macros-icon macros-play');
    play.setAttribute('aria-label', `Play ${macro.name}`);
    play.title = 'Play on the session showing (Esc stops it)';
    actions.append(play);
    const keys = button('⌨', () => this.#captureKeys(macro, row), 'macros-icon');
    keys.setAttribute('aria-label', `Keys for ${macro.name}`);
    keys.title = macro.keys ? `${macro.keys} plays it: change` : 'Give it a key combination';
    actions.append(keys);
    const save = button('⇧', () => void this.#export(macro), 'macros-icon');
    save.setAttribute('aria-label', `Export ${macro.name}`);
    save.title = 'Export as a .mac file, for ACS';
    actions.append(save);
    if (macro.mine) {
      const share = button(
        '⇪',
        () => openMacroShareDialog(macro, () => void this.refresh()),
        'macros-icon',
      );
      share.setAttribute('aria-label', `Share ${macro.name}`);
      share.title = 'Share';
      const rename = button('✎', () => this.#rename(macro, row), 'macros-icon');
      rename.setAttribute('aria-label', `Rename ${macro.name}`);
      rename.title = 'Rename';
      const remove = button('×', () => void this.#delete(macro), 'macros-icon');
      remove.setAttribute('aria-label', `Delete ${macro.name}`);
      remove.title = 'Delete';
      // Demo guests have no one to share with.
      if ('demo' in document.documentElement.dataset) actions.append(rename, remove);
      else actions.append(share, rename, remove);
    }
    return row;
  }

  /** True while a row waits for a key combination: the page's own Alt keys stand aside. */
  get capturing(): boolean {
    return this.#capturing;
  }

  #capturing = false;

  /**
   * Waits on the row for Alt+Shift and a letter or digit, which then plays the macro; Delete
   * takes its keys away, Esc leaves them.
   */
  #captureKeys(macro: MacroInfo, row: HTMLElement): void {
    const box = h('span', {
      class: 'macros-capture',
      tabindex: '0',
      role: 'textbox',
      'aria-label': `Keys for ${macro.name}`,
      text: 'Press Alt+Shift and a letter or digit. Delete clears, Esc cancels.',
    });
    row.replaceChildren(box);
    this.#capturing = true;
    const done = (keys: string | null | undefined) => {
      this.#capturing = false;
      if (keys === undefined) {
        void this.refresh();
        return;
      }
      void api('PUT', `/api/macros/${macro.id}/keys`, { keys }).then(
        () => {
          this.#status.textContent = keys
            ? `${keys} plays ${macro.name}.`
            : `${macro.name} has no keys now.`;
          return this.refresh();
        },
        (error: unknown) => {
          this.#status.textContent = (error as Error).message;
          return this.refresh();
        },
      );
    };
    box.addEventListener('keydown', (event) => {
      event.preventDefault();
      event.stopPropagation();
      if (event.key === 'Escape') done(undefined);
      else if (event.key === 'Delete' || event.key === 'Backspace') done(null);
      else {
        const key = /^(?:Key|Digit)([A-Z0-9])$/.exec(event.code)?.[1];
        if (event.altKey && event.shiftKey && !event.ctrlKey && !event.metaKey && key)
          done(`Alt+Shift+${key}`);
      }
    });
    box.addEventListener('blur', () => {
      if (this.#capturing) done(undefined);
    });
    box.focus();
  }

  /** Shows the recording in progress, or none. */
  recording(state: { name: string; screens: number } | undefined): void {
    this.#recordBar.hidden = state === undefined;
    if (state)
      this.#recordText.textContent = `● Recording ${state.name} · ${state.screens} ${state.screens === 1 ? 'screen' : 'screens'}`;
  }

  /** Says something in the panel's status line. */
  say(text: string): void {
    this.#status.textContent = text;
  }

  #startNew(mode: 'add' | 'record'): void {
    this.#mode = mode;
    this.#submit.textContent = mode === 'add' ? 'Add' : 'Record';
    this.#name.setAttribute(
      'aria-label',
      mode === 'add' ? 'New macro name' : 'Name for the recording',
    );
    this.#form.hidden = false;
    this.#name.value = '';
    this.#name.focus();
  }

  /** A .mac file as a new macro of the user's, named as the file names it (made unique). */
  async #import(chosen: File): Promise<void> {
    const content = await chosen.text();
    const macro = parseHodMacro(content);
    if (typeof macro === 'string') {
      this.#status.textContent = macro;
      return;
    }
    if (holdsPassword(macro)) {
      this.#status.textContent =
        'This macro holds a typed password, and GreenGlow never keeps one. In ACS, make it a prompt, then import it again.';
      return;
    }
    const base = (macro.name || chosen.name.replace(/\.[^.]*$/, '')).trim().slice(0, 50) || 'Macro';
    for (let n = 1; n < 10; n++) {
      const name = n === 1 ? base : `${base} (${n})`;
      try {
        await api('POST', '/api/macros', { name, description: macro.description, content });
        this.#status.textContent = `Imported ${name}.`;
        break;
      } catch (error) {
        if (error instanceof ApiError && error.status === 409) continue;
        this.#status.textContent = (error as Error).message;
        break;
      }
    }
    await this.refresh();
  }

  /** Saves a macro as a .mac file, as ACS saves one. */
  async #export(macro: MacroInfo): Promise<void> {
    try {
      const { content } = await api<{ content: string }>('GET', `/api/macros/${macro.id}`);
      const link = h('a', {
        href: URL.createObjectURL(new Blob([content], { type: 'text/xml' })),
        download: `${macro.name.replace(/[\\/:*?"<>|]+/g, '').trim() || 'macro'}.mac`,
      });
      link.click();
      setTimeout(() => URL.revokeObjectURL(link.href), 1000);
    } catch (error) {
      this.#status.textContent = (error as Error).message;
    }
  }

  async #create(name: string): Promise<void> {
    try {
      await api('POST', '/api/macros', { name });
      this.#form.hidden = true;
      this.#status.textContent = `Made ${name.trim()}.`;
    } catch (error) {
      this.#status.textContent = (error as Error).message;
    }
    await this.refresh();
  }

  /** Renames in place: the name becomes a box; Enter saves, Esc puts it back. */
  #rename(macro: MacroInfo, row: HTMLElement): void {
    const input = h('input', {
      class: 'macros-input',
      type: 'text',
      maxlength: '60',
      value: macro.name,
      'aria-label': `New name for ${macro.name}`,
    });
    row.replaceChildren(input);
    input.focus();
    input.select();
    input.addEventListener('keydown', (event) => {
      if (event.key === 'Enter') {
        event.preventDefault();
        void api('PUT', `/api/macros/${macro.id}`, { name: input.value }).then(
          () => this.refresh(),
          (error: unknown) => (this.#status.textContent = (error as Error).message),
        );
      } else if (event.key === 'Escape') {
        event.preventDefault();
        event.stopPropagation();
        void this.refresh();
      }
    });
  }

  async #delete(macro: MacroInfo): Promise<void> {
    if (!confirm(`Delete the macro ${macro.name}?`)) return;
    try {
      await api('DELETE', `/api/macros/${macro.id}`);
      this.#status.textContent = `Deleted ${macro.name}.`;
    } catch (error) {
      this.#status.textContent = (error as Error).message;
    }
    await this.refresh();
  }
}
