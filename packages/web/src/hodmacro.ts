// Host-on-Demand macros: the XML ACS saves (.mac files, an HAScript element), read into a model
// that playing and recording work on, and written back. A macro is a set of screens; each says
// how to recognise it (its description), what to do there (its actions) and which screens may
// come next. Values are HOD expressions: text in single quotes ('USER'), host keys in brackets
// inside text ('[enter]').
//
// Only what GreenGlow plays is modelled; other elements are kept as they are (`other`), so a
// macro read and written back loses nothing it had.

export interface HodMacro {
  name: string;
  description: string;
  /** Milliseconds to wait for a next screen before giving up. */
  timeout: number;
  /** Milliseconds to pause between screens. */
  pauseTime: number;
  /** The HAScript element's other attributes, kept for writing back. */
  attributes: Record<string, string>;
  screens: HodScreen[];
}

export interface HodScreen {
  name: string;
  entry: boolean;
  exit: boolean;
  transient: boolean;
  description: HodDescriptor[];
  actions: HodAction[];
  next: string[];
  /** Milliseconds to wait for one of the next screens; 0 uses the macro's timeout. */
  nextTimeout: number;
}

/** How a screen is recognised. `optional` and `invert` as HOD's optional and invertmatch. */
export type HodDescriptor = (
  | {
      kind: 'string';
      value: string;
      row: number;
      col: number;
      erow: number;
      ecol: number;
      caseSense: boolean;
    }
  | { kind: 'cursor'; row: number; col: number }
  | { kind: 'oia'; status: string }
  | { kind: 'numfields'; number: number }
  | { kind: 'numinputfields'; number: number }
  | { kind: 'other'; xml: string }
) & { optional: boolean; invert: boolean };

export type HodAction =
  | {
      kind: 'input';
      value: string;
      row: number;
      col: number;
      moveCursor: boolean;
      xlateHostKeys: boolean;
      /** HOD's stored password: GreenGlow refuses a macro with one. */
      encrypted: boolean;
    }
  | {
      kind: 'prompt';
      name: string;
      row: number;
      col: number;
      len: number;
      default: string;
      encrypted: boolean;
      clearField: boolean;
    }
  | { kind: 'pause'; value: number }
  | { kind: 'mouseclick'; row: number; col: number }
  | { kind: 'message'; title: string; value: string }
  | { kind: 'other'; xml: string };

/** Reads a macro, or says why it can't. */
export function parseHodMacro(xml: string): HodMacro | string {
  const doc = new DOMParser().parseFromString(xml, 'application/xml');
  if (doc.getElementsByTagName('parsererror').length > 0) return 'This file isn’t well-formed XML.';
  const root = doc.documentElement;
  if (root.localName !== 'HAScript') return 'This isn’t a Host-on-Demand macro (no HAScript).';
  const attributes: Record<string, string> = {};
  for (const attribute of [...root.attributes])
    if (!['name', 'description', 'timeout', 'pausetime'].includes(attribute.name))
      attributes[attribute.name] = attribute.value;
  return {
    name: root.getAttribute('name') ?? '',
    description: root.getAttribute('description') ?? '',
    timeout: num(root.getAttribute('timeout'), 60000),
    pauseTime: num(root.getAttribute('pausetime'), 300),
    attributes,
    screens: children(root, 'screen').map(readScreen),
  };
}

function readScreen(screen: Element): HodScreen {
  const next = children(screen, 'nextscreens')[0];
  return {
    name: screen.getAttribute('name') ?? '',
    entry: bool(screen.getAttribute('entryscreen')),
    exit: bool(screen.getAttribute('exitscreen')),
    transient: bool(screen.getAttribute('transient')),
    description: [...(children(screen, 'description')[0]?.children ?? [])].map(readDescriptor),
    actions: [...(children(screen, 'actions')[0]?.children ?? [])].map(readAction),
    next: next ? children(next, 'nextscreen').map((n) => n.getAttribute('name') ?? '') : [],
    nextTimeout: num(next?.getAttribute('timeout') ?? null, 0),
  };
}

function readDescriptor(element: Element): HodDescriptor {
  const a = (name: string) => element.getAttribute(name);
  const flags = { optional: bool(a('optional')), invert: bool(a('invertmatch')) };
  switch (element.localName) {
    case 'string':
      return {
        kind: 'string',
        value: a('value') ?? '',
        row: num(a('row'), 0),
        col: num(a('col'), 0),
        erow: num(a('erow'), 0),
        ecol: num(a('ecol'), 0),
        caseSense: bool(a('casesense')),
        ...flags,
      };
    case 'cursor':
      return { kind: 'cursor', row: num(a('row'), 0), col: num(a('col'), 0), ...flags };
    case 'oia':
      return { kind: 'oia', status: a('status') ?? 'NOTINHIBITED', ...flags };
    case 'numfields':
    case 'numinputfields':
      return { kind: element.localName, number: num(a('number'), 0), ...flags };
    default:
      return { kind: 'other', xml: new XMLSerializer().serializeToString(element), ...flags };
  }
}

function readAction(element: Element): HodAction {
  const a = (name: string) => element.getAttribute(name);
  switch (element.localName) {
    case 'input':
      return {
        kind: 'input',
        value: a('value') ?? '',
        row: num(a('row'), 0),
        col: num(a('col'), 0),
        moveCursor: a('movecursor') !== 'false',
        xlateHostKeys: a('xlatehostkeys') !== 'false',
        encrypted: bool(a('encrypted')),
      };
    case 'prompt':
      return {
        kind: 'prompt',
        name: a('name') ?? '',
        row: num(a('row'), 0),
        col: num(a('col'), 0),
        len: num(a('len'), 0),
        default: a('default') ?? '',
        encrypted: bool(a('encrypted')),
        clearField: bool(a('clearfield')),
      };
    case 'pause':
      return { kind: 'pause', value: num(a('value'), 0) };
    case 'mouseclick':
      return { kind: 'mouseclick', row: num(a('row'), 0), col: num(a('col'), 0) };
    case 'message':
      return { kind: 'message', title: a('title') ?? '', value: a('value') ?? '' };
    default:
      return { kind: 'other', xml: new XMLSerializer().serializeToString(element) };
  }
}

/** Writes a macro as ACS saves it. */
export function writeHodMacro(macro: HodMacro): string {
  const lines = [
    `<HAScript ${attrs({
      name: macro.name,
      description: macro.description,
      timeout: String(macro.timeout),
      pausetime: String(macro.pauseTime),
      ...macro.attributes,
    })}>`,
  ];
  for (const screen of macro.screens) {
    lines.push(
      `    <screen ${attrs({
        name: screen.name,
        entryscreen: String(screen.entry),
        exitscreen: String(screen.exit),
        transient: String(screen.transient),
      })}>`,
      '        <description>',
      ...screen.description.map((d) => `            ${writeDescriptor(d)}`),
      '        </description>',
      '        <actions>',
      ...screen.actions.map((action) => `            ${writeAction(action)}`),
      '        </actions>',
      `        <nextscreens ${attrs({ timeout: String(screen.nextTimeout) })}>`,
      ...screen.next.map((name) => `            <nextscreen ${attrs({ name })} />`),
      '        </nextscreens>',
      '    </screen>',
    );
  }
  lines.push('</HAScript>', '');
  return lines.join('\n');
}

function writeDescriptor(d: HodDescriptor): string {
  if (d.kind === 'other') return d.xml;
  const flags = { optional: String(d.optional), invertmatch: String(d.invert) };
  switch (d.kind) {
    case 'string':
      return `<string ${attrs({
        value: d.value,
        row: String(d.row),
        col: String(d.col),
        erow: String(d.erow),
        ecol: String(d.ecol),
        casesense: String(d.caseSense),
        wrap: 'false',
        ...flags,
      })} />`;
    case 'cursor':
      return `<cursor ${attrs({ row: String(d.row), col: String(d.col), ...flags })} />`;
    case 'oia':
      return `<oia ${attrs({ status: d.status, ...flags })} />`;
    case 'numfields':
    case 'numinputfields':
      return `<${d.kind} ${attrs({ number: String(d.number), ...flags })} />`;
  }
}

function writeAction(action: HodAction): string {
  switch (action.kind) {
    case 'input':
      return `<input ${attrs({
        value: action.value,
        row: String(action.row),
        col: String(action.col),
        movecursor: String(action.moveCursor),
        xlatehostkeys: String(action.xlateHostKeys),
        encrypted: String(action.encrypted),
      })} />`;
    case 'prompt':
      return `<prompt ${attrs({
        name: action.name,
        description: '',
        row: String(action.row),
        col: String(action.col),
        len: String(action.len),
        default: action.default,
        clearfield: String(action.clearField),
        encrypted: String(action.encrypted),
        movecursor: 'true',
        xlatehostkeys: 'true',
        assigntovar: '',
        varupdateonly: 'false',
      })} />`;
    case 'pause':
      return `<pause ${attrs({ value: String(action.value) })} />`;
    case 'mouseclick':
      return `<mouseclick ${attrs({ row: String(action.row), col: String(action.col) })} />`;
    case 'message':
      return `<message ${attrs({ title: action.title, value: action.value })} />`;
    case 'other':
      return action.xml;
  }
}

/** A HOD string expression's text: 'USER' is USER, with '' for a quote inside. */
export function hodText(value: string): string {
  const quoted = /^'(.*)'$/s.exec(value);
  return quoted ? quoted[1].replace(/''/g, "'") : value;
}

/** Text as a HOD string expression. */
export function hodString(text: string): string {
  return `'${text.replace(/'/g, "''")}'`;
}

/** Whether a macro holds a typed password (an encrypted input): GreenGlow never keeps one. */
export function holdsPassword(macro: HodMacro): boolean {
  return macro.screens.some((screen) =>
    screen.actions.some(
      (action) => action.kind === 'input' && action.encrypted && action.value !== '',
    ),
  );
}

function children(parent: Element, name: string): Element[] {
  return [...parent.children].filter((child) => child.localName === name);
}

function num(value: string | null, fallback: number): number {
  const n = Number(value);
  return value !== null && value !== '' && Number.isFinite(n) ? n : fallback;
}

function bool(value: string | null): boolean {
  return value === 'true';
}

function attrs(values: Record<string, string>): string {
  return Object.entries(values)
    .map(([name, value]) => `${name}="${escape(value)}"`)
    .join(' ');
}

function escape(value: string): string {
  return value
    .replace(/&/g, '&amp;')
    .replace(/"/g, '&quot;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;');
}
