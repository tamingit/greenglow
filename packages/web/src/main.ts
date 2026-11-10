import {
  formatResponseCode,
  type ConnectionEvent,
  type Key,
  type ScreenSnapshot,
} from './protocol/index.ts';
import { SOLO_MODIFIERS, mapKey } from './keymap.ts';
import { LOOKS, loadLook, lookById, saveLook, type Look } from './looks/look.ts';
import { Offline, type OfflineChoice } from './offline.ts';
import { attachPointer } from './pointer.ts';
import { Renderer, type RuleLine, type ScreenSize } from './renderer.ts';
import { Selection, copyText } from './selection.ts';
import { liveTable, readTable, toTsv } from './copytable.ts';
import { lobby } from './lobby.ts';
import { openShareDialog, sharedCount } from './share.ts';
import { SharingPanel } from './sharing.ts';
import { askValue, showNotice, type Notice } from './notice.ts';
import { Invites } from './invites.ts';
import { describeEnd, type Ended } from './ended.ts';
import {
  RemoteSession,
  SessionRefused,
  openSession,
  type Session,
  type ControlState,
  type SessionInfo,
  type SessionListener,
  type PasteResult,
} from './session.ts';
import { ContextMenu } from './contextmenu.ts';
import { MAX_TABS, TabStrip, type TabView } from './tabs.ts';
import { KeyPanel } from './keypanel.ts';
import { api } from './admin/api.ts';
import { MacrosPanel } from './macros.ts';
import { PrinterPanel, type PrinterSystem } from './printer.ts';
import { MacroRecorder } from './recorder.ts';
import { parseHodMacro, writeHodMacro } from './hodmacro.ts';
import { playMacro, type PlayerHost } from './player.ts';
import { UserFeed } from './userfeed.ts';
import { applyTheme, loadTheme, openThemeDialog, saveTheme } from './themes.ts';
import { SoftKeyboard } from './softkeys.ts';
import './style.css';
import './terminal.css';
import './classic.css';
import './greenglow.css';
import './night-shift.css';
import './offline.css';
import './tabs.css';
import './side.css';

interface TargetInfo {
  name: string;
  /** What the picker shows; the name if the server has no label for it. */
  label?: string;
  secure: boolean;
  /** For tls://: whether the server checks the IBM i's certificate against a CA. */
  verified?: boolean;
  /** The colour an administrator gave the system (#rrggbb). */
  color?: string | null;
  /** The user's own colour for it, which wins. */
  myColor?: string | null;
}

const fullscreenButton = element<HTMLButtonElement>('#fullscreen');
const lookGroup = element<HTMLElement>('#look');
const ruleLineGroup = element<HTMLElement>('#rule-line');
const screenSizeGroup = element<HTMLElement>('#screen-size');
const fontSizeRow = element<HTMLElement>('#font-size-row');
const fontSizeSelect = element<HTMLSelectElement>('#font-size');
const menu = element<HTMLDetailsElement>('#bar-menu');
const menuStatus = element<HTMLElement>('#menu-status');
const menuStatusText = element<HTMLElement>('#menu-status-text');
const menuFullscreen = element<HTMLButtonElement>('#menu-fullscreen');
const statusChip = element<HTMLElement>('#status');
const statusText = element<HTMLElement>('#status-text');
const inUse = element<HTMLElement>('#in-use');
const inUseMessage = element<HTMLElement>('#in-use-message');
const retryButton = element<HTMLButtonElement>('#retry');
const spareButton = element<HTMLButtonElement>('#spare');
const stage = element<HTMLElement>('#stage');
const terminal = element<HTMLElement>('#terminal');
const logPanel = element<HTMLElement>('#log-panel');
const logList = element<HTMLOListElement>('#log');
const logClose = element<HTMLButtonElement>('#log-close');
const menuLog = element<HTMLButtonElement>('#menu-log');
const menuLogLabel = element<HTMLElement>('#menu-log-label');
const control = element<HTMLElement>('#control');
const controlText = element<HTMLElement>('#control-text');
const takeControl = element<HTMLButtonElement>('#take-control');
const menuShare = element<HTMLButtonElement>('#menu-share');
const menuDisconnect = element<HTMLButtonElement>('#menu-disconnect');
const menuSharing = element<HTMLButtonElement>('#menu-sharing');
const menuSharingLabel = element<HTMLElement>('#menu-sharing-label');
const userName = element<HTMLElement>('#user-name');
const userUpn = element<HTMLElement>('#user-upn');
const appBar = element<HTMLElement>('.app-bar');
const colorSection = element<HTMLElement>('#color-section');
const colorLabel = element<HTMLElement>('#color-label');
const myColor = element<HTMLInputElement>('#my-color');
const colorReset = element<HTMLButtonElement>('#color-reset');

/** localStorage, or nothing if the browser blocks it. */
function storage(): Storage | undefined {
  try {
    return window.localStorage;
  } catch {
    return undefined;
  }
}

const look = loadLook(storage(), location.search);
const renderer = new Renderer(terminal, stage, look);
const selection = new Selection({ show: (rect) => renderer.select(rect) });
const softKeyboard = new SoftKeyboard({
  terminal,
  grid: element<HTMLElement>('#terminal .grid'),
  positionAt: (x, y) => renderer.positionAt(x, y),
  live: () => active.session?.screen,
  press(key) {
    selection.clear();
    press(key);
  },
});

/** Puts the keys on the screen, keeping a touch screen's on-screen keyboard up if it is. */
function focusScreen(): void {
  if (softKeyboard.open) softKeyboard.focus();
  else terminal.focus();
}
const offline = new Offline(terminal, {
  connect: () => {
    if (targets.has(active.target)) void connect(active.target);
  },
  choose(name) {
    chooseTarget(name);
    void connect(name);
  },
  disconnect() {
    active.confirming = false;
    active.session?.close();
    focusScreen();
  },
  end() {
    active.confirming = false;
    void endSession(active);
    focusScreen();
  },
  stay() {
    active.confirming = false;
    active.closing = false;
    showOffline();
    focusScreen();
  },
});
offline.blank = true;
const strip = new TabStrip(element<HTMLElement>('#tab-strip'), {
  select(id, focus) {
    const tab = tabs.find((t) => t.id === id);
    if (tab) activate(tab, focus);
  },
  close(id) {
    const tab = tabs.find((t) => t.id === id);
    if (tab) closeTab(tab);
  },
  add: () => newSession(),
});

/** How the status dot shows the session: grey, amber, green or red. */
type StatusState = 'idle' | 'busy' | 'ok' | 'warn' | 'error';

/**
 * One session tab: its 5250 session (or none yet) and everything the page shows about it.
 * The page shows the active tab; the others keep their sessions attached in the background.
 */
interface Tab {
  readonly id: number;
  /** The connection it is for, by name: chosen in the bar or on the card until it connects. */
  target: string;
  session: Session | undefined;
  /** Counts the sessions opened in this tab, so a closed one's late events are ignored. */
  generation: number;
  /** True while a session is open in it, with something to lose on unload. */
  live: boolean;
  /** True while a live session is open; the Connect button then disconnects. */
  connected: boolean;
  /** True from Connect until the new session's first screen. */
  awaiting: boolean;
  /**
   * The status to show at the session's first screen if Connecting or Joining still shows.
   * A new session's startup reply normally replaces it first, but it may come before this
   * page attached; a joined session started earlier and sends none.
   */
  joined: string | undefined;
  /** This page's part in the session: the owner's, or a share's. */
  role: SessionInfo['role'];
  /** Whether this page holds the session's keyboard; only it may type. */
  hasKeyboard: boolean;
  /** Who holds the keyboard, as the server last said. */
  control: ControlState | undefined;
  /** For the owner: how many people the session is shared with now. */
  sharedWith: number;
  /** True while Disconnect waits for the user to confirm it. */
  confirming: boolean;
  /** Close the tab once its session ends: Close was asked for. */
  closing: boolean;
  /** Past the sign-on screen, as the server says with each screen. */
  signedOn: boolean;
  /** How the last live session ended, for the overlay. */
  ended: { label: string; code: number; reason: string } | undefined;
  /** The screen's title as the server reads it: the tab's name. */
  title: string;
  status: { text: string; state: StatusState };
  log: string[];
  /** A device-in-use notice waiting for an answer. */
  inUse: { target: string; label: string; deviceName: string } | undefined;
  /** A Page Down or Page Up the user pressed, until its page arrives and slides in. */
  rolled: 'up' | 'down' | undefined;
  /** Called with each new screen while Copy entire table waits for the next page. */
  screenWaiter: ((snapshot: ScreenSnapshot) => void) | undefined;
}

let nextTabId = 1;
function newTab(target: string): Tab {
  return {
    id: nextTabId++,
    target,
    session: undefined,
    generation: 0,
    live: false,
    connected: false,
    awaiting: false,
    joined: undefined,
    role: 'owner',
    hasKeyboard: true,
    control: undefined,
    sharedWith: 0,
    confirming: false,
    closing: false,
    signedOn: false,
    ended: undefined,
    title: '',
    status: idleStatus(),
    log: [],
    inUse: undefined,
    screenWaiter: undefined,
    rolled: undefined,
  };
}

/** The tabs, in order. There is always at least one. */
const tabs: Tab[] = [];
/** The tab the page shows. */
let active: Tab;
/** How long the server keeps a session no browser is attached to. */
let idleMinutes = 0;
/** How many browsers may show one session. */
let maxFrontends = 2;
/** Demo mode (the server says so): a guest, with no sharing and no administration. */
let demo = false;
/** The connections this user may use, by name; undefined until they are loaded. */
const targets = new Map<string, TargetInfo>();
let targetsLoaded = false;

function element<T extends Element>(selector: string): T {
  const found = document.querySelector<T>(selector);
  if (!found) throw new Error(`Missing element ${selector}`);
  return found;
}

/** The page chrome follows the look: light for GreenGlow, dark for Night Shift. */
function showLook(chosen: Look): void {
  document.documentElement.dataset.look = chosen.id;
  for (const input of lookGroup.querySelectorAll('input'))
    input.checked = input.value === chosen.id;
}

/** A new tab's status: not connected, or why it can't be. */
function idleStatus(): Tab['status'] {
  if (!targetsLoaded) return { text: 'Loading targets…', state: 'idle' };
  return targets.size
    ? { text: 'Not connected.', state: 'idle' }
    : {
        text: 'No systems are available to you. Ask an administrator for access.',
        state: 'error',
      };
}

/** Sets a tab's status; the bar shows the active tab's. */
function setStatus(text: string, state: StatusState, tab = active): void {
  tab.status = { text, state };
  if (tab === active) paintStatus();
}

function paintStatus(): void {
  const { text, state } = active.status;
  statusText.textContent = text;
  statusChip.title = text;
  statusChip.dataset.state = state;
  // The bar may cut the message short; the menu always shows all of it.
  menuStatusText.textContent = text;
  menuStatus.dataset.state = state;
}

/** How a target's connection is protected, for the bar's badge and the overlay's tiles. */
function securityOf(target: TargetInfo): Omit<OfflineChoice, 'name' | 'label'> {
  if (!target.secure)
    return {
      security: 'plain',
      securityText: 'Not encrypted',
      securityTitle: 'Plain Telnet: sign-on passwords cross the network unencrypted',
    };
  if (target.verified === false)
    return {
      security: 'unverified',
      securityText: 'TLS, unverified',
      securityTitle:
        'Encrypted, but the IBM i’s certificate isn’t checked: the server has no CA to verify it with',
    };
  return { security: 'tls', securityText: 'TLS', securityTitle: 'Telnet over verified TLS' };
}

/** The system's colour on the page: the user's own, else the administrator's. */
function colorOf(target: TargetInfo | undefined): string | undefined {
  return target?.myColor ?? target?.color ?? undefined;
}

/** The active tab's system's colour: the band under the bar and the menu's colour. */
function showSecurity(): void {
  showColor(targets.get(active.target));
}

/** The band under the bar, and the menu's colour for the system. */
function showColor(target: TargetInfo | undefined): void {
  const color = colorOf(target);
  appBar.toggleAttribute('data-colored', color !== undefined);
  if (color) appBar.style.setProperty('--system-color', color);
  colorSection.hidden = target === undefined;
  if (!target) return;
  colorLabel.textContent = `Colour for ${labelOf(target.name)}`;
  // A colour input can't be empty: with no colour yet, it starts on the bar's green.
  myColor.value = color ?? '#2f7d4f';
  colorReset.hidden = !target.myColor;
}

/** Keeps the user's own colour for the active tab's system on the server; null for the system's. */
async function saveColor(color: string | null): Promise<void> {
  const target = targets.get(active.target);
  if (!target) return;
  const response = await fetch(`/api/targets/${encodeURIComponent(target.name)}/color`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ color }),
  });
  if (!response.ok) {
    setStatus(`Could not save the colour (${response.status}).`, 'warn');
    return;
  }
  target.myColor = color;
  showSecurity();
  showTabs();
  showOffline();
}

/** The most lines a tab's log keeps. */
const LOG_LINES = 1000;

function logLine(text: string, tab = active): void {
  tab.log.push(text);
  if (tab.log.length > LOG_LINES) tab.log.shift();
  if (tab !== active) return;
  // Follow new lines unless the reader has scrolled up to look at older ones.
  const atEnd = logList.scrollHeight - logList.scrollTop - logList.clientHeight < 8;
  logList.append(logItem(text));
  if (logList.childElementCount > LOG_LINES) logList.firstElementChild?.remove();
  if (atEnd) logList.scrollTop = logList.scrollHeight;
}

function logItem(text: string): HTMLLIElement {
  const item = document.createElement('li');
  item.textContent = text;
  return item;
}

function paintLog(): void {
  logList.replaceChildren(...active.log.map(logItem));
  logList.scrollTop = logList.scrollHeight;
}

const LOG_KEY = 'greenglow.log-open';

/**
 * The session log is hidden unless asked for. Open, it docks under the screen and the
 * screen refits smaller above it. The choice is kept per browser.
 */
function showLog(open: boolean): void {
  logPanel.hidden = !open;
  menuLog.setAttribute('aria-expanded', String(open));
  menuLogLabel.textContent = open ? 'Hide session log' : 'Show session log';
  if (open) logList.scrollTop = logList.scrollHeight;
  try {
    storage()?.setItem(LOG_KEY, open ? 'open' : 'closed');
  } catch {
    // Storage blocked or full: the choice still applies to this page.
  }
}

function setConnected(tab: Tab, open: boolean): void {
  tab.connected = open;
  showTabs();
  if (tab !== active) return;
  paintBar();
  showOffline();
}

/** The bar and the notices for the active tab. */
function paintBar(): void {
  menuDisconnect.hidden = !active.connected;
  menuShare.hidden = demo || !(active.connected && active.live && active.role === 'owner');
  showSecurity();
  paintStatus();
  paintControl();
  paintInUse();
}

/** What the tab strip shows for a tab: its screen's title, or its system before one. */
function tabView(tab: Tab): TabView {
  const system = tab.target ? labelOf(tab.target) : '';
  return {
    id: tab.id,
    title: tab.title || (tab.session ? system : 'New session'),
    system: system || 'No system chosen',
    color: colorOf(targets.get(tab.target)),
    connected: tab.connected,
    security: (() => {
      const target = targets.get(tab.target);
      if (!target) return undefined;
      const { security, securityText, securityTitle } = securityOf(target);
      return { state: security, text: securityText, title: securityTitle };
    })(),
  };
}

function showTabs(): void {
  strip.show(tabs.map(tabView), active.id);
}

/**
 * The overlay: Connecting until the first screen, the question while Disconnect waits for
 * an answer, and Disconnected with no session.
 */
function showOffline(): void {
  const label = labelOf(active.target);
  if (active.connected && active.confirming) {
    // Those it is shared with can view it only while its owner is in it.
    const shared = active.sharedWith;
    const sharedNote =
      shared === 0
        ? ''
        : ` ${shared === 1 ? 'The person it’s shared with loses' : `The ${shared} people it’s shared with lose`} ` +
          'it until you’re back in it.';
    offline.show({
      state: 'confirm',
      title: `${active.closing ? 'Close' : 'Disconnect from'} ${label}?`,
      detail:
        (idleMinutes > 0
          ? `You’re still signed on. Left running, the session waits ${minutes(idleMinutes)} on ` +
            'the server for you to come back to it. Ending it now drops your IBM i job, and the ' +
            'program you’re in may end with an error: sign off first to end it cleanly.'
          : 'You’re still signed on. Disconnecting ends the session and drops your IBM i job, ' +
            'and the program you’re in may end with an error. Sign off first to end it cleanly.') +
        sharedNote,
    });
    return;
  }
  if (active.connected && !active.awaiting) {
    offline.hide();
    return;
  }
  if (active.connected) {
    offline.show({ state: 'connecting', detail: `Opening a session with ${label}.` });
    return;
  }
  showLobby();
  const ended = active.ended;
  // Demo guests are told what to type on the fake IBM i.
  let detail = demo
    ? 'Demo: the fake IBM i shows placeholder screens and takes any user name and password.'
    : 'No session is open.';
  if (targets.size === 0) detail = active.status.text;
  const end = ended && describeEnd(ended.code, ended.reason, ended.label);
  if (targets.size > 0 && ended)
    detail = end ? end.message : `You disconnected from ${ended.label}.`;
  offline.show({
    state: 'disconnected',
    detail,
    error: end?.error === true,
    action: label ? `Connect to ${label}` : 'Connect',
    disabled: targets.size === 0,
    choices: [...targets.values()].map((target) => ({
      name: target.name,
      label: labelOf(target.name),
      ...securityOf(target),
      color: colorOf(target),
    })),
    chosen: active.target,
  });
}

/** A screen for a tab: its title names the tab, and the active tab's is painted. */
function show(tab: Tab, snapshot: ScreenSnapshot, title: string): void {
  const retitled = tab.title !== title;
  tab.title = title;
  if (tab.awaiting) {
    tab.awaiting = false;
    if (tab.joined !== undefined && tab.status.state === 'busy') setStatus(tab.joined, 'ok', tab);
    tab.joined = undefined;
    if (tab === active) {
      paintBar();
      showOffline();
    }
  }
  if (retitled) showTabs();
  if (tab === active) paint(snapshot);
}

// Paint at most once a frame, with the latest snapshot.
let latest: ScreenSnapshot | undefined;
function paint(snapshot: ScreenSnapshot): void {
  offline.blank = false;
  terminal.classList.remove('no-screen');
  if (latest === undefined) {
    requestAnimationFrame(() => {
      if (latest) {
        selection.see(latest);
        renderer.render(latest);
        slideIn(latest);
      }
      latest = undefined;
    });
  }
  latest = snapshot;
}

/** The page a Page Down or Page Up brought: its table's rows slide in, as if scrolled. */
function slideIn(snapshot: ScreenSnapshot): void {
  const direction = active.rolled;
  if (!direction || snapshot.keyboard === 'locked') return;
  active.rolled = undefined;
  const table = snapshot.keyboard === 'unlocked' ? liveTable(snapshot)?.subfile : undefined;
  if (table) renderer.slide(direction, table.records[0], table.lastRow);
}

/** The active tab's screen; before it has one, the sign-on silhouette under the card. */
function showScreen(): void {
  const screen = active.session?.screen;
  if (screen) {
    paint(screen);
    return;
  }
  latest = undefined;
  offline.blank = true;
  terminal.classList.add('no-screen');
}

let audio: AudioContext | undefined;
function alarm(): void {
  renderer.flash();
  try {
    audio ??= new AudioContext();
    const tone = audio.createOscillator();
    const volume = audio.createGain();
    tone.frequency.value = 880;
    volume.gain.value = 0.04;
    tone.connect(volume).connect(audio.destination);
    tone.start();
    tone.stop(audio.currentTime + 0.08);
  } catch {
    // No audio: the flash is enough.
  }
}

function describe(event: ConnectionEvent): string | undefined {
  switch (event.type) {
    case 'negotiated':
      return 'Telnet negotiation complete';
    case 'startup':
      return `Startup ${event.result.code}: ${event.result.message} (device ${event.result.deviceName || 'none'})`;
    case 'device-mismatch':
      return `The IBM i used device ${event.actual}, not ${event.requested}`;
    case 'query-answered':
      return 'Answered the 5250 QUERY';
    case 'keyboard-stuck':
      return 'A key is waiting: the host wants input but left the keyboard locked';
    case 'unsupported':
      return `Not handled yet: ${event.what}`;
    case 'data-stream-error':
    case 'protocol-error':
      return `Error: ${event.message}`;
    case 'negative-response':
      return `Sent negative response ${formatResponseCode(event.code)}`;
    default:
      return undefined;
  }
}

/**
 * Listens to a session in `tab`. `label` names it in messages; `target` is the connection's
 * name, to reconnect to. Events from a session the tab has since replaced are ignored.
 */
function listener(tab: Tab, label: string, target = label): SessionListener {
  const generation = ++tab.generation;
  const current = () => tab.generation === generation;
  return {
    screen(snapshot, title, signedOn) {
      if (!current()) return;
      const signedOff = tab.signedOn && !signedOn;
      tab.signedOn = signedOn;
      show(tab, snapshot, title);
      tab.screenWaiter?.(snapshot);
      if (signedOff) closeSignedOff(tab, label);
    },
    event(event) {
      if (!current()) return;
      if (event.type === 'alarm' && tab === active) alarm();
      if (event.type === 'startup')
        setStatus(`${label}: ${event.result.message}.`, event.result.ok ? 'ok' : 'error', tab);
      if (event.type === 'device-in-use') showInUse(tab, target, label, event.deviceName);
      const text = describe(event);
      if (text) logLine(text, tab);
    },
    control(state) {
      if (!current()) return;
      const had = tab.control?.you;
      // The owner's count of people shared with: read when it attaches, and again whenever a
      // browser comes or goes (the owner's other browser may have changed the shares).
      if (tab.role === 'owner' && tab.control?.frontends !== state.frontends) refreshShared(tab);
      tab.control = state;
      tab.hasKeyboard = state.you;
      // When the keyboard changes hands, the status line says so too, replacing any warning
      // about it from a key pressed while someone else had it.
      if (had !== undefined && had !== state.you && tab.role !== 'view')
        setStatus(
          state.you
            ? 'You have the keyboard.'
            : `${state.holder ?? 'Someone else'} has the keyboard. Take control to type.`,
          state.you ? 'ok' : 'warn',
          tab,
        );
      if (tab === active) paintControl();
    },
    pasted(result) {
      if (current()) reportPaste(result, tab);
    },
    role(role) {
      if (!current()) return;
      tab.role = role;
      if (role === 'view') tab.hasKeyboard = false;
      setStatus(
        role === 'view'
          ? 'The owner changed your access to view only.'
          : 'The owner gave you full access. Take control to type.',
        'warn',
        tab,
      );
      if (tab === active) paintBar();
    },
    closed(code, reason) {
      if (!current()) return;
      tab.control = undefined;
      const end = describeEnd(code, reason, label);
      if (!tab.inUse)
        setStatus(end ? `${end.title}.` : 'Disconnected.', end?.error ? 'error' : 'idle', tab);
      // Lost without the user's asking: say why, in a popup, not only in the status line.
      if (end && !tab.inUse && !tab.closing) alertEnded(end);
      tab.live = false;
      tab.awaiting = false;
      tab.joined = undefined;
      tab.confirming = false;
      tab.ended = { label, code, reason };
      setConnected(tab, false);
      if (tab.closing) removeTab(tab);
    },
  };
}

let endedNotice: Notice | undefined;
/** Says why a session left the page; several at once (a server restart) share one notice. */
function alertEnded(end: Ended): void {
  if (endedNotice?.open) {
    endedNotice.add('Sessions ended', end.message);
    return;
  }
  endedNotice = showNotice(end.title, end.message, () =>
    active.connected ? focusScreen() : offline.focus(),
  );
}

/** Sends a signed-out visitor to sign in, then back to this page. */
function signIn(): never {
  location.assign(
    `/auth/signin?returnUrl=${encodeURIComponent(location.pathname + location.search)}`,
  );
  throw new Error('Signing in');
}

/** The signed-in user, shown in the menu. */
async function loadUser(): Promise<void> {
  const response = await fetch('/api/me');
  if (response.status === 401) signIn();
  if (!response.ok) throw new Error(`/api/me returned ${response.status}`);
  const me = (await response.json()) as {
    upn: string;
    name: string;
    idleMinutes: number;
    isAdmin: boolean;
    maxFrontends: number;
    demo?: boolean;
  };
  maxFrontends = me.maxFrontends;
  demo = me.demo === true;
  element<HTMLElement>('#menu-admin').hidden = !me.isAdmin || demo;
  if (demo) {
    // Guests have no one to share with: no Share panel, tab or menu item.
    document.documentElement.dataset.demo = '';
    menuSharing.hidden = true;
    for (const tab of sideTabs) if (tab.dataset.side === 'sharing') tab.hidden = true;
    if (openSide === 'sharing') showSide(undefined);
  }
  idleMinutes = me.idleMinutes;
  userName.textContent = me.name;
  userUpn.textContent = me.upn;
}

async function loadTargets(): Promise<void> {
  const response = await fetch('/api/targets');
  if (response.status === 401) signIn();
  if (!response.ok) throw new Error(`/api/targets returned ${response.status}`);
  const list = (await response.json()) as { targets: TargetInfo[] };
  for (const target of list.targets) {
    targets.set(target.name, target);
  }
  targetsLoaded = true;
  for (const tab of tabs) {
    if (!targets.has(tab.target)) tab.target = defaultTarget();
    if (!tab.session) tab.status = idleStatus();
  }
  paintBar();
  showTabs();
  showOffline();
  followPrinter();
}

/** A new tab's system: the one this browser connected to last, else the first. */
function defaultTarget(): string {
  const last = loadTarget();
  if (last !== undefined && targets.has(last)) return last;
  return targets.keys().next().value ?? '';
}

function labelOf(target: string): string {
  return targets.get(target)?.label ?? target;
}

const TARGET_KEY = 'greenglow.target';

/** The system this browser connected to last, chosen first next time. */
function loadTarget(): string | undefined {
  try {
    return storage()?.getItem(TARGET_KEY) ?? undefined;
  } catch {
    return undefined;
  }
}

function saveTarget(target: string): void {
  try {
    storage()?.setItem(TARGET_KEY, target);
  } catch {
    // Storage blocked or full: the first system is chosen next time.
  }
}

/** The active tab's system, chosen in the bar or on the card before it connects. */
function chooseTarget(name: string): void {
  active.target = name;
  showSecurity();
  showTabs();
  showOffline();
  followPrinter();
}

/** The notice's title for each reason the server gives for not opening a session. */
const REFUSED_TITLE: Record<string, string> = {
  limit: 'Session limit reached',
  'server-full': 'The server is full',
  'no-device-name': 'No device name free',
  draining: 'This server is restarting',
};

/**
 * Opens a session on the server in `tab`, which picks the device name: one kept for the user's
 * reconnect, else the next free one, or a spare after a device-in-use when `spare` is set.
 */
async function connect(target: string, spare = false, tab = active): Promise<void> {
  tab.inUse = undefined;
  if (tab === active) paintInUse();
  tab.target = target;
  // A tile on the card connects at once: the Printer panel moves to that system with it.
  if (tab === active) followPrinter();
  saveTarget(target);
  let info;
  try {
    info = await openSession(target, spare);
  } catch (error) {
    if (!(error instanceof SessionRefused)) throw error;
    // A popup, not the status line, where it was easily missed.
    showNotice(REFUSED_TITLE[error.code] ?? 'Can’t open a session', error.message, focusScreen);
    return;
  }
  tab.session?.close();
  tab.log = [];
  if (tab === active) paintLog();
  const deviceName = info.device;
  tab.session = new RemoteSession(info.id, listener(tab, labelOf(target), target));
  resetControl(tab, 'owner');
  tab.live = true;
  tab.awaiting = true;
  tab.joined = `${labelOf(target)}: connected as ${deviceName ?? 'an assigned device'}.`;
  tab.confirming = false;
  tab.title = '';
  tab.signedOn = false;
  setConnected(tab, true);
  setStatus(
    `Connecting to ${labelOf(target)} as ${deviceName ?? 'an assigned device'}…`,
    'busy',
    tab,
  );
  if (tab === active) focusScreen();
}

/**
 * Attaches `tab` to a session the server is holding: the user's own, or one shared with them.
 * `takeOver` sends away the user's browser showing it elsewhere when it has no room.
 */
function join(info: SessionInfo, tab = active, takeOver = false): void {
  tab.inUse = undefined;
  tab.session?.close();
  tab.log = [];
  if (targets.has(info.target)) tab.target = info.target;
  tab.session = new RemoteSession(
    info.id,
    listener(tab, info.label, info.target),
    info.shareToken,
    takeOver,
  );
  resetControl(tab, info.role);
  tab.live = true;
  tab.awaiting = true;
  tab.confirming = false;
  tab.title = info.title;
  tab.signedOn = false;
  if (tab === active) {
    paintLog();
    showScreen();
    followPrinter();
  }
  setConnected(tab, true);
  const whose = info.role === 'owner' ? 'your session' : `${info.owner}’s session`;
  const access =
    info.role === 'owner' ? '' : ` (${info.role === 'view' ? 'view only' : 'full access'})`;
  setStatus(`Joining ${whose} on ${info.label}${access}…`, 'busy', tab);
  tab.joined = `${info.label}: joined ${whose}${access}.`;
  if (tab === active) focusScreen();
}

/**
 * Joins several sessions: the first in this tab if it has no session, the others in new tabs,
 * as many as there is room for.
 */
function joinAll(sessions: SessionInfo[]): void {
  let left = 0;
  for (const info of sessions) {
    const tab = active.connected ? addTab(info.target) : active;
    if (!tab) {
      left++;
      continue;
    }
    join(info, tab);
  }
  if (left > 0)
    setStatus(
      `${left} more ${left === 1 ? 'session' : 'sessions'} didn’t fit: at most ${MAX_TABS} tabs.`,
      'warn',
    );
}

const OPEN_KEY = 'greenglow.open';

/** The sessions this window had open before a reload, in tab order. */
function loadOpen(): string[] {
  try {
    const saved: unknown = JSON.parse(window.sessionStorage.getItem(OPEN_KEY) ?? '[]');
    return Array.isArray(saved) ? saved.filter((id) => typeof id === 'string') : [];
  } catch {
    return [];
  }
}

window.addEventListener('pagehide', () => {
  try {
    const open = tabs.flatMap((t) => (t.connected && t.session?.id ? [t.session.id] : []));
    window.sessionStorage.setItem(OPEN_KEY, JSON.stringify(open));
  } catch {
    // Storage blocked: a reload rejoins them oldest first.
  }
});

// Back from another page (the admin page) out of the back/forward cache: the sockets were
// closed meanwhile, so load the page afresh, which joins its sessions again.
window.addEventListener('pageshow', (event) => {
  if (event.persisted) location.reload();
});

/**
 * At load, joins the user's own sessions that none of their browsers shows (someone they shared
 * it with may be watching), a tab each: those this window had before a reload in their order,
 * then the rest oldest first. Sessions shared with the user aren't joined; they are offered in
 * the corner.
 */
async function rejoinLeft(): Promise<void> {
  const before = loadOpen();
  const place = (id: string) => {
    const at = before.indexOf(id);
    return at < 0 ? before.length : at;
  };
  // A reloaded page's sessions may still count the page that left until the server sees its
  // socket close (later through a proxy), so those are asked about again for a few seconds.
  for (let asked = 0; asked < 10; asked++) {
    if (asked > 0) await new Promise((resolve) => setTimeout(resolve, 500));
    const sessions = await listSessions();
    const open = openSessionIds();
    const mine = sessions.filter((s) => s.role === 'owner' && !open.has(s.id));
    joinAll(
      mine
        .filter((s) => !s.ownerPresent)
        .sort((a, b) => place(a.id) - place(b.id) || a.createdAt.localeCompare(b.createdAt)),
    );
    if (!mine.some((s) => s.ownerPresent && before.includes(s.id))) return;
  }
}

/**
 * Joins a session someone shared, offered in the corner: in this tab if it has no session,
 * else in a new one.
 */
function joinShared(info: SessionInfo): void {
  if (!active.connected) {
    join(info);
    return;
  }
  const tab = addTab(info.target);
  if (!tab) {
    showNotice(
      'Session limit reached',
      `At most ${MAX_TABS} sessions in one page: close one to join this one.`,
      focusScreen,
    );
    return;
  }
  activate(tab);
  join(info, tab);
}
const invites = new Invites(joinShared);

/** The user's own sessions and those shared with them, as the server holds them now. */
async function listSessions(): Promise<SessionInfo[]> {
  const response = await fetch('/api/sessions');
  if (!response.ok) throw new Error(`/api/sessions returned ${response.status}`);
  return ((await response.json()) as { sessions: SessionInfo[] }).sessions;
}

/** The user's sessions as the server last listed them (pushed over /ws/user). */
let lastSessions: SessionInfo[] = [];

/**
 * A new session list from the server: offers any newly shared with the user, and lists those
 * to go back to on the Disconnected card, except those open in tabs.
 */
function applySessions(sessions: SessionInfo[]): void {
  lastSessions = sessions;
  invites.update(sessions, openSessionIds());
  sharedWithMe = sessions.filter((s) => s.role !== 'owner');
  showSharingFacts();
  showLobby();
  // The tab's session may have signed on, as someone else than before.
  followPrinter();
}

/** The Disconnected card's list, from the last list the server sent. */
function showLobby(): void {
  if (active.connected) return;
  const open = openSessionIds();
  offline.lobby = lobby(
    lastSessions.filter((s) => !open.has(s.id)),
    maxFrontends,
    {
      join: (info) => join(info),
      takeOver: (info) => join(info, active, true),
      joinAll,
      end: (info) =>
        void fetch(`/api/sessions/${encodeURIComponent(info.id)}`, { method: 'DELETE' }),
    },
  );
}

/** Asks for the list now, after the user changed something; the server pushes it anyway. */
async function refreshLobby(): Promise<void> {
  try {
    applySessions(await listSessions());
  } catch {
    // Offline: the feed brings the list when the server is back.
  }
}

function openSessionIds(): Set<string> {
  return new Set(tabs.flatMap((t) => (t.connected && t.session?.id ? [t.session.id] : [])));
}

// Others share, open or end sessions (another person, another tab, the idle timeout): the
// server pushes the list as it changes. The socket also tells the server this user has
// GreenGlow open, for the lights in an owner's sharing panel.
const userFeed = new UserFeed(applySessions);

/** Sessions shared with the user, as the server last listed them. */
let sharedWithMe: SessionInfo[] = [];

const sharing = new SharingPanel(element<HTMLElement>('#sharing-panel'), {
  takeControl: () => {
    if (active.session instanceof RemoteSession) active.session.takeControl();
  },
  returnControl: () => {
    if (active.session instanceof RemoteSession) active.session.returnControl();
  },
  invite: () => shareActive(),
  join: (info) => joinShared(info),
  leave: (info) => {
    const tab = tabs.find((t) => t.connected && t.session?.id === info.id);
    if (tab) disconnect(tab);
  },
  changed: () => {
    void refreshLobby();
    refreshShared(active);
  },
  closed: () => {
    showSide(undefined);
    focusScreen();
  },
});

/** What the sharing panel shows for the active tab and the user. */
function showSharingFacts(): void {
  const id = active.session?.id;
  sharing.update({
    session:
      id === undefined
        ? undefined
        : { id, role: active.role, live: active.connected, control: active.control },
    shared: sharedWithMe,
    open: openSessionIds(),
  });
}

// The keys panel: F1 to F24 and the keys most used.
const menuKeysPanel = element<HTMLButtonElement>('#menu-keys-panel');
const menuKeysPanelLabel = element<HTMLElement>('#menu-keys-panel-label');
const keyPanel = new KeyPanel(
  element<HTMLElement>('#keys-panel'),
  // Back to the screen after a press, even when the focus was in the panel (Other keys).
  (name) => {
    press({ type: 'function', name });
    focusScreen();
  },
  () => {
    showSide(undefined);
    focusScreen();
  },
);

// The side panels: tabs on the window's right edge (Keys, Share, Macros, Printer), each
// pulling its panel out; one at a time, kept per browser (side.css).
type Side = 'keys' | 'sharing' | 'macros' | 'printer';
const SIDES: readonly Side[] = ['keys', 'sharing', 'macros', 'printer'];
const SIDE_KEY = 'greenglow.side';
const sideTabs = [...document.querySelectorAll<HTMLButtonElement>('.side-tab')];
const macrosRoot = element<HTMLElement>('#macros-panel');
const macros = new MacrosPanel(
  macrosRoot,
  () => {
    showSide(undefined);
    focusScreen();
  },
  {
    start: startRecording,
    stop: stopRecording,
    cancel: () => endRecording('Recording cancelled.'),
    play: (macro) => void playById(macro),
    listed: (list) => {
      macroKeys.clear();
      for (const macro of list) if (macro.keys) macroKeys.set(macro.keys, macro);
    },
  },
);

// Printer output: the user's spooled files on the system of the tab showing (printer.ts).
const printerRoot = element<HTMLElement>('#printer-panel');
const printer = new PrinterPanel(printerRoot, () => {
  showSide(undefined);
  focusScreen();
});

/**
 * The panel follows the tab's system and IBM i user: when the systems load, one is chosen or
 * connected to, a session is joined, the tab changes, or its session signs on.
 */
function followPrinter(): void {
  if (openSide === 'printer') void printer.show(printerSystem());
}

/**
 * The system of the tab showing, for the Printer output panel, and the IBM i user its session
 * is signed on as when the session is the user's own (a shared one is its owner's user).
 */
function printerSystem(): PrinterSystem | undefined {
  const target = targets.get(active.target);
  if (!target) return undefined;
  const id = active.session?.id;
  const mine = id ? lastSessions.find((s) => s.id === id && s.role === 'owner') : undefined;
  return {
    name: target.name,
    label: target.label ?? target.name,
    ibmUser: mine?.ibmUser ?? undefined,
  };
}

/** The user's macro key combinations (Alt+Shift and a letter or digit), to the macro each plays. */
const macroKeys = new Map<string, { id: string; name: string }>();

// Playing a macro (player.ts) on the tab showing when ▶ was pressed: the user asked for it, so
// its keys go to the host as theirs. Their own keys wait meanwhile, and Esc stops it.
let playing: { cancel: boolean; wake: (() => void) | undefined } | undefined;

async function playById(info: { id: string; name: string }): Promise<void> {
  const tab = active;
  if (playing) return;
  if (!tab.connected || !tab.session?.screen) {
    macros.say('Open a session to play it on first.');
    return;
  }
  if (!mayType()) return;
  let macro;
  try {
    const { content } = await api<{ content: string }>('GET', `/api/macros/${info.id}`);
    macro = parseHodMacro(content);
  } catch (error) {
    macros.say((error as Error).message);
    return;
  }
  if (typeof macro === 'string') {
    macros.say(macro);
    return;
  }
  const run = (playing = { cancel: false, wake: undefined as (() => void) | undefined });
  setStatus(`Playing ${info.name}… Esc stops it.`, 'busy', tab);
  focusScreen();
  const sleep = (ms: number) =>
    new Promise<void>((resolve) => {
      const timer = setTimeout(done, ms);
      run.wake = done;
      function done() {
        clearTimeout(timer);
        run.wake = undefined;
        resolve();
      }
    });
  const host: PlayerHost = {
    screen: () => tab.session?.screen,
    press: (key) => tab.session?.key(key),
    waitFor: (match, ms) =>
      new Promise((resolve) => {
        const now = tab.session?.screen;
        if (now && now.keyboard !== 'locked' && match(now)) {
          resolve(now);
          return;
        }
        const finish = (screen: ScreenSnapshot | undefined) => {
          clearTimeout(timer);
          tab.screenWaiter = undefined;
          run.wake = undefined;
          resolve(screen);
        };
        const timer = setTimeout(() => finish(undefined), ms);
        run.wake = () => finish(undefined);
        tab.screenWaiter = (screen) => {
          if (screen.keyboard !== 'locked' && match(screen)) finish(screen);
        };
      }),
    answer: (ms) => {
      const before = tab.session?.screen;
      return before ? nextScreen(tab, before, ms) : Promise.resolve(undefined);
    },
    ask: (prompt) =>
      askValue(info.name, prompt.name, prompt.default, prompt.secret).then((value) => {
        focusScreen();
        return value;
      }),
    tell: (title, message) =>
      new Promise((resolve) => {
        showNotice(title, message, () => {
          focusScreen();
          resolve();
        });
      }),
    sleep,
    cancelled: () => run.cancel,
  };
  let result;
  try {
    result = await playMacro(macro, host);
  } finally {
    playing = undefined;
  }
  const skipped = result.skipped.length
    ? ` It skipped what GreenGlow doesn’t play yet: ${result.skipped.join(', ')}.`
    : '';
  if (result.done) setStatus(`Played ${info.name}.${skipped}`, 'ok', tab);
  else setStatus(`Stopped ${info.name}: ${result.stopped}.${skipped}`, 'warn', tab);
}

// Recording a macro (recorder.ts): the keys the user presses on the tab it started in.
let recording: { recorder: MacroRecorder; tab: Tab } | undefined;

function startRecording(name: string): string | undefined {
  if (!name) return 'Name the macro first.';
  if (recording) return 'A macro is being recorded already.';
  if (!active.connected || !active.session?.screen) return 'Open a session to record on first.';
  recording = { recorder: new MacroRecorder(name), tab: active };
  document.documentElement.toggleAttribute('data-recording', true);
  showRecording();
  setStatus(`Recording the macro ${name}. Stop it in the Macros panel.`, 'busy');
  focusScreen();
  return undefined;
}

function showRecording(): void {
  macros.recording(
    recording && { name: recording.recorder.name, screens: recording.recorder.screens },
  );
}

/** A key or paste the user made, on the tab being recorded, before it goes to the host. */
function record(what: Key | string): void {
  const screen = active.session?.screen;
  if (!recording || recording.tab !== active || !screen) return;
  if (typeof what === 'string') recording.recorder.paste(what, screen);
  else recording.recorder.key(what, screen);
  showRecording();
}

async function stopRecording(): Promise<void> {
  if (!recording) return;
  const { recorder, tab } = recording;
  const macro = recorder.finish(tab.session?.screen);
  try {
    await api('POST', '/api/macros', { name: recorder.name, content: writeHodMacro(macro) });
    endRecording(`Saved the macro ${recorder.name}: ${macro.screens.length} screens.`);
  } catch (error) {
    // Kept recording, so nothing is lost: another name can be tried by stopping again later.
    macros.say((error as Error).message);
  }
}

function endRecording(said: string): void {
  recording = undefined;
  document.documentElement.toggleAttribute('data-recording', false);
  showRecording();
  macros.say(said);
  setStatus(said, 'ok');
  void macros.refresh();
}
let openSide: Side | undefined;

function showSide(side: Side | undefined): void {
  if (side === 'sharing' && demo) side = undefined;
  openSide = side;
  keyPanel.open = side === 'keys';
  if (side === 'sharing') {
    sharing.open();
    showSharingFacts();
  } else sharing.close();
  macrosRoot.hidden = side !== 'macros';
  if (side === 'macros') void macros.refresh();
  printerRoot.hidden = side !== 'printer';
  if (side === 'printer') void printer.show(printerSystem());
  if (side) document.documentElement.dataset.side = side;
  else delete document.documentElement.dataset.side;
  for (const tab of sideTabs) tab.setAttribute('aria-expanded', String(tab.dataset.side === side));
  menuSharing.setAttribute('aria-expanded', String(side === 'sharing'));
  menuSharingLabel.textContent = side === 'sharing' ? 'Hide sharing panel' : 'Show sharing panel';
  menuKeysPanel.setAttribute('aria-expanded', String(side === 'keys'));
  menuKeysPanelLabel.textContent = side === 'keys' ? 'Hide keys panel' : 'Show keys panel';
  try {
    storage()?.setItem(SIDE_KEY, side ?? '');
  } catch {
    // Storage blocked or full: the choice still applies to this page.
  }
}

/** Pulls a panel out, or pushes it back if it is out. The sharing panel takes the keys. */
function toggleSide(side: Side): void {
  const opening = openSide !== side;
  showSide(opening ? side : undefined);
  if (opening && side === 'sharing') sharing.focus();
  if (opening && side === 'printer') printer.focus();
  else focusScreen();
}
for (const tab of sideTabs)
  tab.addEventListener('click', () => toggleSide(tab.dataset.side as Side));
menuKeysPanel.addEventListener('click', () => {
  menu.open = false;
  toggleSide('keys');
});
menuSharing.addEventListener('click', () => {
  menu.open = false;
  toggleSide('sharing');
});

function showInUse(tab: Tab, target: string, label: string, deviceName: string): void {
  tab.inUse = { target, label, deviceName };
  setStatus(`${label}: device in use.`, 'warn', tab);
  if (tab === active) paintInUse();
}

function paintInUse(): void {
  const tab = active;
  inUse.hidden = tab.inUse === undefined;
  if (!tab.inUse) return;
  const { target, deviceName } = tab.inUse;
  inUseMessage.textContent =
    `Device ${deviceName} is still in use by an earlier session. It frees up when the IBM i ` +
    'drops that session. A spare name connects now, but the earlier job can’t be reached from it ' +
    'and ends on its own.';
  retryButton.onclick = () => void connect(target, false, tab);
  spareButton.onclick = () => void connect(target, true, tab);
}

// Keys. A lone Ctrl acts on release (Reset or New Line), as in ACS, so Ctrl+Enter
// (Field Exit) doesn't also reset.
let soloModifier: string | undefined;
/** Whether this page may type into the active session; if not, the status says why. */
function mayType(): boolean {
  if (!active.live || active.hasKeyboard) return true;
  setStatus(
    active.role === 'view'
      ? 'View only: you can watch this session but not type.'
      : `${controlText.textContent ?? 'Someone else has the keyboard'}. Take control to type.`,
    'warn',
  );
  return false;
}

function press(key: Key): void {
  if (copying) {
    setStatus('Copying the table. Press Esc to stop.', 'warn');
    return;
  }
  if (playing) {
    setStatus('Playing a macro. Press Esc to stop it.', 'warn');
    return;
  }
  if (!mayType()) return;
  if (key.type === 'function' && (key.name === 'RollUp' || key.name === 'RollDown'))
    active.rolled = key.name === 'RollUp' ? 'up' : 'down';
  record(key);
  active.session?.key(key);
}

/** Set while Copy entire table pages through a list; the user's keys wait, and Esc stops it. */
let copying: { cancel: boolean } | undefined;
/** The most pages Copy entire table goes through. */
const MAX_COPY_PAGES = 200;

/**
 * The next screen in `tab` after a key: once the host has answered (the keyboard unlocks, or
 * shows an error) with a screen unlike `before`, or with any screen after locking the
 * keyboard. The page may never see the locked screen when the host answers at once, since only
 * the latest screen reaches it. An answer that looks just like `before` (a message saying
 * there is no more, say) counts after a short wait. Undefined if nothing comes within `ms`.
 */
function nextScreen(
  tab: Tab,
  before: ScreenSnapshot,
  ms = 20_000,
): Promise<ScreenSnapshot | undefined> {
  return new Promise((resolve) => {
    let taken = false;
    let same: ReturnType<typeof setTimeout> | undefined;
    const done = (snapshot: ScreenSnapshot | undefined) => {
      clearTimeout(timer);
      clearTimeout(same);
      tab.screenWaiter = undefined;
      resolve(snapshot);
    };
    const timer = setTimeout(() => done(undefined), ms);
    tab.screenWaiter = (snapshot) => {
      clearTimeout(same);
      if (snapshot.keyboard === 'locked') taken = true;
      else if (taken || snapshot.keyboard === 'error' || !sameBuffer(snapshot, before))
        done(snapshot);
      else same = setTimeout(() => done(snapshot), 2000);
    };
  });
}

function sameBuffer(a: ScreenSnapshot, b: ScreenSnapshot): boolean {
  return a.buffer.length === b.buffer.length && a.buffer.every((byte, i) => byte === b.buffer[i]);
}

/**
 * Copies the table on the screen as tab-separated text. With `entire`, pages on with Page
 * Down (the user asked for it) and copies each page until Bottom, leaving the screen on the
 * last page; it stops early at an error, a screen with no table, a page that didn't change,
 * MAX_COPY_PAGES, or Esc. A page with no marker (More... or Bottom) gives no sign of the end,
 * so there an error or an unchanged page is the end of the list, not an early stop. Paging
 * ends with Reset, which the user asked for, to clear an error the last Page Down left.
 */
async function copyTable(entire: boolean): Promise<void> {
  const tab = active;
  const session = tab.session;
  const first = session?.screen ? readTable(session.screen) : undefined;
  if (!session || !first) {
    setStatus('There is no table on this screen to copy.', 'warn');
    return;
  }
  if (copying) return;
  const records = [...first.records];
  let pages = 1;
  let stopped: string | undefined;
  /** Why paging ended on a page with no marker. */
  let ended: string | undefined;
  if (entire && first.more) {
    if (!mayType()) return;
    const run = (copying = { cancel: false });
    let last = first;
    /** The screen before each Page Down. */
    let shown = session.screen as ScreenSnapshot;
    try {
      while (last.more) {
        if (run.cancel) {
          stopped = 'you pressed Esc';
          break;
        }
        if (pages >= MAX_COPY_PAGES) {
          stopped = `it reached ${MAX_COPY_PAGES} pages`;
          break;
        }
        setStatus(`Copying the table: page ${pages + 1}… Esc stops.`, 'busy', tab);
        const waiting = nextScreen(tab, shown);
        session.key({ type: 'function', name: 'RollUp' });
        const next = await waiting;
        const table = next && next.keyboard !== 'error' ? readTable(next) : undefined;
        const refused = next?.keyboard === 'error';
        const same = table !== undefined && table.key === last.key;
        if (!last.marked && (refused || same)) {
          ended = refused ? 'the IBM i refused Page Down' : 'the page stopped changing';
          break;
        }
        if (!next) stopped = 'the next page didn’t come';
        else if (refused) stopped = 'the IBM i refused Page Down';
        else if (!table) stopped = 'the next screen has no table';
        else if (same) stopped = 'the next page was the same';
        if (stopped || !table) break;
        records.push(...table.records);
        pages++;
        last = table;
        shown = next as ScreenSnapshot;
      }
    } finally {
      // Reset stays in the terminal (it never reaches the IBM i): it clears the error a
      // refused Page Down leaves, so the keyboard is free again.
      session.key({ type: 'function', name: 'Reset' });
      copying = undefined;
    }
  }
  const rows = records.length === 1 ? '1 row' : `${records.length} rows`;
  const from = pages === 1 ? '' : ` from ${pages} pages`;
  const why = stopped
    ? ` It stopped early because ${stopped}.`
    : ended
      ? ` The screen shows no More... or Bottom, so it went on until ${ended}.`
      : '';
  if (await copyText(toTsv(first.headings, records), terminal))
    setStatus(`Copied the table: ${rows}${from}.${why}`, stopped ? 'warn' : 'ok', tab);
  else setStatus('This browser didn’t let the page copy. Try again with Alt+C.', 'warn', tab);
}

/**
 * Pasted text, typed from the cursor by the server as the user's own keys: each further line
 * in the same column one row down, a tab to the next field, never Enter.
 */
function pasteText(text: string): void {
  if (!active.session?.screen || text.length === 0 || !mayType()) return;
  selection.clear();
  record(text);
  active.session.paste(text);
}

function reportPaste(result: PasteResult, tab: Tab): void {
  const count = (n: number) => (n === 1 ? '1 character' : `${n} characters`);
  if (result.stopped === null) {
    setStatus(`Pasted ${count(result.typed)}.`, 'ok', tab);
    return;
  }
  const why = {
    refused: 'the screen refused the next one (a protected position, or the field’s rules)',
    keyboard: 'the keyboard locked (the screen was sent, or is busy)',
    'screen-end': 'the rest would fall below the screen',
  }[result.stopped];
  setStatus(`Pasted ${result.typed} of ${count(result.of)}: ${why}.`, 'warn', tab);
}

/** Who has the keyboard, shown when more than one browser shows the session or it is shared. */
function paintControl(): void {
  showSharingFacts();
  const state = active.control;
  if (!state || !active.connected) {
    control.hidden = true;
    return;
  }
  const role = active.role;
  control.hidden = !(role !== 'owner' || state.frontends > 1);
  controlText.textContent =
    role === 'view'
      ? 'View only'
      : state.you
        ? 'You have the keyboard'
        : `${state.holder ?? 'No one'} has the keyboard`;
  takeControl.hidden = role === 'view' || state.you;
}

function resetControl(tab: Tab, role: SessionInfo['role']): void {
  tab.role = role;
  tab.hasKeyboard = role === 'owner';
  tab.control = undefined;
  tab.sharedWith = 0;
  if (tab === active) paintBar();
}

/** Reads how many people the owner's session in a tab is shared with, for the top bar. */
function refreshShared(tab: Tab): void {
  const session = tab.session;
  if (tab.role !== 'owner' || !(session instanceof RemoteSession) || !session.id) return;
  sharedCount(session.id).then(
    (count) => {
      if (tab.session !== session) return;
      tab.sharedWith = count;
      if (tab === active) paintBar();
    },
    () => undefined,
  );
}

function shareActive(): void {
  const tab = active;
  if (tab.session?.id)
    openShareDialog(tab.session.id, labelOf(tab.target), () => refreshShared(tab));
}

takeControl.addEventListener('click', () => {
  if (active.session instanceof RemoteSession) active.session.takeControl();
  focusScreen();
});
menuShare.addEventListener('click', () => {
  menu.open = false;
  shareActive();
});
menuDisconnect.addEventListener('click', () => {
  menu.open = false;
  disconnect();
});
terminal.addEventListener('keydown', (event) => {
  if (copying && event.key === 'Escape') {
    event.preventDefault();
    copying.cancel = true;
    return;
  }
  if (playing && event.key === 'Escape') {
    event.preventDefault();
    playing.cancel = true;
    playing.wake?.();
    return;
  }
  // With no session, Tab leaves the screen for the overlay's Connect button.
  if (event.key === 'Tab' && !active.connected) return;
  if (event.code in SOLO_MODIFIERS) {
    if (!event.repeat) soloModifier = event.code;
    event.preventDefault();
    return;
  }
  soloModifier = undefined;
  // Ctrl+C (Ctrl+Insert) copies a selection and sends nothing; with none, the keys are
  // the browser's and the map's, as before.
  if (copyKey(event) && selection.rect) {
    event.preventDefault();
    void copySelection();
    return;
  }
  const key = mapKey(event);
  if (!key) return;
  event.preventDefault();
  selection.clear();
  press(key);
});
terminal.addEventListener('keyup', (event) => {
  if (event.code !== soloModifier) return;
  soloModifier = undefined;
  event.preventDefault();
  selection.clear();
  press({ type: 'function', name: SOLO_MODIFIERS[event.code] });
});
terminal.addEventListener('blur', () => (soloModifier = undefined));

function copyKey(event: KeyboardEvent): boolean {
  return (
    event.ctrlKey &&
    !event.altKey &&
    !event.shiftKey &&
    !event.metaKey &&
    (event.code === 'KeyC' || event.code === 'Insert')
  );
}

/** The selected text, from the active tab's screen. */
function selectedText(): string | undefined {
  const screen = active.session?.screen;
  return screen && selection.text(screen);
}

async function copySelection(): Promise<void> {
  const text = selectedText();
  if (text === undefined) return;
  if (!(await copyText(text, terminal))) terminal.title = 'Copying is blocked here.';
}

// Ctrl+V: the browser fires a paste event at the terminal, with the text and no permission
// prompt. The key map leaves Ctrl+V alone for this.
terminal.addEventListener('paste', (event) => {
  const text = event.clipboardData?.getData('text/plain');
  if (text === undefined) return;
  event.preventDefault();
  pasteText(text);
});

/** Selects the whole screen, the message line included. */
function selectAll(): void {
  const screen = active.session?.screen;
  if (!screen) return;
  selection.drag(
    { row: 1, column: 1 },
    { row: screen.rows + (screen.messageLine ? 1 : 0), column: screen.columns },
  );
}

async function pasteFromClipboard(): Promise<void> {
  try {
    pasteText(await navigator.clipboard.readText());
  } catch {
    setStatus(
      'This browser didn’t let the page read the clipboard. Press Ctrl+V to paste.',
      'warn',
    );
  }
  terminal.focus();
}

// The right-click menu, over a screen. Elsewhere (the overlay, no session) the browser's own.
const contextMenu = new ContextMenu(terminal);
terminal.addEventListener('contextmenu', (event) => {
  const overScreen = (event.target as Element).closest('.offline') === null;
  if (!active.session?.screen || !overScreen) return;
  event.preventDefault();
  const table = readTable(active.session.screen);
  contextMenu.show(
    [
      {
        label: 'Copy',
        shortcut: 'Ctrl+C',
        disabled: !selection.rect,
        run: () => void copySelection(),
      },
      { label: 'Paste', shortcut: 'Ctrl+V', run: () => void pasteFromClipboard() },
      { label: 'Select all', run: selectAll },
      // Only when the screen has a table.
      ...(table
        ? [
            { label: 'Copy table', shortcut: 'Alt+Shift+C', run: () => void copyTable(false) },
            {
              label: 'Copy entire table',
              shortcut: 'Alt+C',
              disabled: !table.more,
              run: () => void copyTable(true),
            },
          ]
        : []),
    ],
    event.clientX,
    event.clientY,
  );
});

// A copy from the browser's own menu.
terminal.addEventListener('copy', (event) => {
  const text = selectedText();
  if (text === undefined) return;
  event.preventDefault();
  event.clipboardData?.setData('text/plain', text);
});
attachPointer({ terminal, renderer, selection, live: () => active.session?.screen, press });

/**
 * Disconnect drops the session at once before sign-on, and asks first after it: a dropped
 * job waits on the IBM i, and the program in it may end with an error. Pressing Disconnect
 * again while it asks only brings the question back, so a double click can't skip it.
 */
function disconnect(tab = active): void {
  if (tab.confirming) {
    if (tab !== active) activate(tab);
    offline.focus();
    return;
  }
  // Someone else's session: leaving never ends it.
  if (tab.role !== 'owner') {
    tab.session?.close();
    return;
  }
  const session = tab.session;
  if (session?.open && session.screen && tab.signedOn) {
    tab.confirming = true;
    if (tab !== active) activate(tab);
    else showOffline();
    return;
  }
  // Before sign-on there is nothing worth keeping.
  void endSession(tab);
}

/**
 * The user signed off: the IBM i shows the sign-on screen again on the same connection. Their
 * own session ends and its tab closes, as if they had closed it before signing on; a session
 * shared with them is the owner's to end.
 */
function closeSignedOff(tab: Tab, label: string): void {
  if (tab.role !== 'owner' || !tab.live || tab.confirming) return;
  setStatus(`Signed off ${label}: its tab closed.`, 'ok', tab);
  tab.closing = true;
  void endSession(tab);
}

/** Ends a tab's session on the server, not just this page's view of it. */
async function endSession(tab: Tab): Promise<void> {
  const id = tab.session?.id;
  tab.session?.close();
  if (id === undefined) return;
  const response = await fetch(`/api/sessions/${encodeURIComponent(id)}`, { method: 'DELETE' });
  if (!response.ok && response.status !== 404)
    logLine(`Ending the session failed: ${response.status}`, tab);
}

/** Closing a tab disconnects it first, asking as Disconnect does once signed on. */
function closeTab(tab: Tab): void {
  if (!tab.connected) {
    removeTab(tab);
    return;
  }
  tab.closing = true;
  disconnect(tab);
}

function removeTab(tab: Tab): void {
  const at = tabs.indexOf(tab);
  if (at < 0) return;
  tabs.splice(at, 1);
  if (tabs.length === 0) tabs.push(newTab(defaultTarget()));
  if (tab === active) activate(tabs[Math.min(at, tabs.length - 1)]);
  else showTabs();
}

/** A new tab, if there is room; it isn't shown until activated. */
function addTab(target = defaultTarget()): Tab | undefined {
  if (tabs.length >= MAX_TABS) return undefined;
  const tab = newTab(target);
  tabs.push(tab);
  showTabs();
  return tab;
}

/** The + button and Alt+N: a new tab, which connects at once when there is one system. */
function newSession(): void {
  const tab = addTab();
  if (!tab) {
    showNotice(
      'Session limit reached',
      `At most ${MAX_TABS} sessions in one page: close one to open another.`,
      focusScreen,
    );
    return;
  }
  activate(tab);
  if (targets.size === 1) void connectUnlessOpenElsewhere(tab);
}

/**
 * Connects a new tab at once, unless the user has sessions open outside this window: the card
 * then lists them, to join one rather than open another.
 */
async function connectUnlessOpenElsewhere(tab: Tab): Promise<void> {
  const open = openSessionIds();
  const sessions = await listSessions().catch(() => []);
  if (sessions.some((s) => s.role === 'owner' && !open.has(s.id))) return;
  if (tabs.includes(tab) && !tab.connected) await connect(tab.target, false, tab);
}

/** Shows a tab: its screen, its bar and its card. `focus` puts the keys on its screen. */
function activate(tab: Tab, focus = true): void {
  if (tab !== active) {
    active = tab;
    selection.clear();
    paintLog();
    showScreen();
  }
  paintBar();
  showTabs();
  followPrinter();
  if (focus) focusScreen();
  // After the focus: a card that comes up with a choice puts the focus on it.
  showOffline();
}

// Alt+1 to Alt+9 and Alt+0 go to the first ten tabs; Alt+N opens a new one; Alt+J joins the
// newest session offered as shared with the user; Alt+K, Alt+S, Alt+M and Alt+P pull out or
// push back the Keys, Share, Macros and Printer output panels;
// Alt+C copies the table on the screen, every page of it, and Alt+Shift+C only this page.
// None of them is a 5250 key, and they work from the screen, the card or the bar alike.
window.addEventListener(
  'keydown',
  (event) => {
    if (!event.altKey || event.ctrlKey || event.metaKey) return;
    // A macro's own keys play it, unless a row is waiting to be given keys.
    const key = /^(?:Key|Digit)([A-Z0-9])$/.exec(event.code)?.[1];
    const macro = event.shiftKey && key && !macros.capturing && macroKeys.get(`Alt+Shift+${key}`);
    if (macro) {
      event.preventDefault();
      event.stopPropagation();
      void playById(macro);
      return;
    }
    if (event.code === 'KeyC' && active.session?.screen) {
      event.preventDefault();
      event.stopPropagation();
      void copyTable(!event.shiftKey);
      return;
    }
    if (event.shiftKey) return;
    const digit = /^Digit(\d)$/.exec(event.code);
    if (digit) {
      event.preventDefault();
      event.stopPropagation();
      const tab = tabs[digit[1] === '0' ? 9 : Number(digit[1]) - 1];
      if (tab) activate(tab);
    } else if (event.code === 'KeyN') {
      event.preventDefault();
      event.stopPropagation();
      newSession();
    } else if (event.code === 'KeyJ' && invites.joinNewest()) {
      event.preventDefault();
      event.stopPropagation();
    } else if (event.code === 'KeyK') {
      event.preventDefault();
      event.stopPropagation();
      toggleSide('keys');
    } else if (event.code === 'KeyS') {
      event.preventDefault();
      event.stopPropagation();
      toggleSide('sharing');
    } else if (event.code === 'KeyM') {
      event.preventDefault();
      event.stopPropagation();
      toggleSide('macros');
    } else if (event.code === 'KeyP') {
      event.preventDefault();
      event.stopPropagation();
      toggleSide('printer');
    }
  },
  true,
);

function minutes(n: number): string {
  return n === 1 ? '1 minute' : `${n} minutes`;
}

myColor.addEventListener('change', () => void saveColor(myColor.value));
colorReset.addEventListener('click', () => void saveColor(null));
for (const option of LOOKS) {
  const input = document.createElement('input');
  input.type = 'radio';
  input.name = 'look';
  input.id = `look-${option.id}`;
  input.value = option.id;
  const label = document.createElement('label');
  label.htmlFor = input.id;
  label.textContent = option.name;
  lookGroup.append(input, label);
}
showLook(look);
/** The look showing now, with the user's own colours for it (themes.ts). */
let currentLook = look;
applyTheme(terminal, loadTheme(storage(), look.id));
function chooseLook(chosen: Look): void {
  currentLook = chosen;
  renderer.setLook(chosen);
  saveLook(storage(), chosen);
  showLook(chosen);
  applyTheme(terminal, loadTheme(storage(), chosen.id));
}
lookGroup.addEventListener('change', (event) => {
  const chosen = lookById((event.target as HTMLInputElement).value);
  if (chosen) chooseLook(chosen);
});

/** Customize colours: the dialog for the look showing; a theme loaded for another switches to it. */
function customizeColours(): void {
  menu.open = false;
  const shown = currentLook;
  openThemeDialog(
    {
      terminal,
      look: { id: shown.id, name: shown.name, decorated: shown.decorate !== undefined },
      changed: (theme) => {
        const id = theme?.base ?? shown.id;
        saveTheme(storage(), id, theme);
        if (id === currentLook.id) applyTheme(terminal, theme);
      },
      switchLook: (id) => {
        const next = lookById(id);
        if (!next) return;
        chooseLook(next);
        // Open again, now for the theme's own look.
        setTimeout(customizeColours);
      },
      closed: focusScreen,
    },
    loadTheme(storage(), shown.id),
  );
}
element<HTMLButtonElement>('#menu-theme').addEventListener('click', customizeColours);

// The rule line through the cursor (ACS's crosshair), kept per browser.
const RULE_LINE_KEY = 'greenglow.rule-line';
const RULE_LINES: { id: RuleLine; name: string }[] = [
  { id: 'off', name: 'Off' },
  { id: 'crosshair', name: 'Crosshair' },
  { id: 'horizontal', name: 'Row' },
  { id: 'vertical', name: 'Column' },
];
let ruleLine: RuleLine = 'off';
try {
  const saved = storage()?.getItem(RULE_LINE_KEY);
  ruleLine = RULE_LINES.find((r) => r.id === saved)?.id ?? 'off';
} catch {
  // Storage blocked: no rule line.
}
for (const option of RULE_LINES) {
  const input = document.createElement('input');
  input.type = 'radio';
  input.name = 'rule-line';
  input.id = `rule-line-${option.id}`;
  input.value = option.id;
  input.checked = option.id === ruleLine;
  const label = document.createElement('label');
  label.htmlFor = input.id;
  label.textContent = option.name;
  ruleLineGroup.append(input, label);
}
renderer.setRuleLine(ruleLine);
ruleLineGroup.addEventListener('change', (event) => {
  const chosen = RULE_LINES.find((r) => r.id === (event.target as HTMLInputElement).value);
  if (!chosen) return;
  renderer.setRuleLine(chosen.id);
  try {
    storage()?.setItem(RULE_LINE_KEY, chosen.id);
  } catch {
    // Storage blocked or full: the choice still applies to this page.
  }
});

// The screen's size, kept per browser: scaled to fit, stretched to fill, or a fixed font
// size (and that size, kept for when Fixed is chosen again).
const SCREEN_SIZE_KEY = 'greenglow.screen-size';
const FONT_SIZE_KEY = 'greenglow.font-size';
const SCREEN_SIZES: { id: ScreenSize['mode']; name: string }[] = [
  { id: 'scale', name: 'Scale' },
  { id: 'stretch', name: 'Stretch' },
  { id: 'fixed', name: 'Fixed' },
];
const FONT_SIZES = [10, 11, 12, 13, 14, 15, 16, 18, 20, 22, 24, 26, 28, 32];
let screenMode: ScreenSize['mode'] = 'scale';
let fontSize = 16;
try {
  const saved = storage()?.getItem(SCREEN_SIZE_KEY);
  screenMode = SCREEN_SIZES.find((s) => s.id === saved)?.id ?? 'scale';
  const savedFont = Number(storage()?.getItem(FONT_SIZE_KEY));
  if (FONT_SIZES.includes(savedFont)) fontSize = savedFont;
} catch {
  // Storage blocked: scaled to fit.
}
for (const option of SCREEN_SIZES) {
  const input = document.createElement('input');
  input.type = 'radio';
  input.name = 'screen-size';
  input.id = `screen-size-${option.id}`;
  input.value = option.id;
  input.checked = option.id === screenMode;
  const label = document.createElement('label');
  label.htmlFor = input.id;
  label.textContent = option.name;
  screenSizeGroup.append(input, label);
}
for (const size of FONT_SIZES) {
  const option = document.createElement('option');
  option.value = String(size);
  option.textContent = `${size} px`;
  option.selected = size === fontSize;
  fontSizeSelect.append(option);
}
function applyScreenSize(): void {
  fontSizeRow.hidden = screenMode !== 'fixed';
  renderer.setSize(screenMode === 'fixed' ? { mode: 'fixed', fontSize } : { mode: screenMode });
}
applyScreenSize();
screenSizeGroup.addEventListener('change', (event) => {
  const chosen = SCREEN_SIZES.find((s) => s.id === (event.target as HTMLInputElement).value);
  if (!chosen) return;
  screenMode = chosen.id;
  applyScreenSize();
  try {
    storage()?.setItem(SCREEN_SIZE_KEY, chosen.id);
  } catch {
    // Storage blocked or full: the choice still applies to this page.
  }
});
fontSizeSelect.addEventListener('change', () => {
  fontSize = Number(fontSizeSelect.value);
  applyScreenSize();
  try {
    storage()?.setItem(FONT_SIZE_KEY, String(fontSize));
  } catch {
    // Storage blocked or full: the choice still applies to this page.
  }
});
async function fullScreen(): Promise<void> {
  menu.open = false;
  await terminal.requestFullscreen();
  // In full screen, Chromium can hand keys such as Ctrl+W and F11 to the page.
  const keyboard = (navigator as Navigator & { keyboard?: { lock?: () => Promise<void> } })
    .keyboard;
  await keyboard?.lock?.().catch(() => undefined);
  focusScreen();
}
fullscreenButton.addEventListener('click', () => void fullScreen());
let logSaved: string | null = null;
try {
  logSaved = storage()?.getItem(LOG_KEY) ?? null;
} catch {
  // Storage blocked: start closed.
}
showLog(logSaved === 'open');
try {
  const side = storage()?.getItem(SIDE_KEY);
  // Every panel but Printer reopens where it was left. Printer waits for the user to click it
  // (its tab or Alt+P), because opening it asks for the IBM i password, which must never be
  // asked for on its own when the page loads.
  if (side !== 'printer' && SIDES.includes(side as Side)) showSide(side as Side);
} catch {
  // Storage blocked: start closed.
}
menuLog.addEventListener('click', () => {
  menu.open = false;
  showLog(menuLog.getAttribute('aria-expanded') !== 'true');
});
logClose.addEventListener('click', () => {
  showLog(false);
  focusScreen();
});
menuFullscreen.addEventListener('click', () => void fullScreen());
// Help opens in a new tab; this one goes back to the screen.
element<HTMLAnchorElement>('#menu-help').addEventListener('click', () => {
  menu.open = false;
});
// The menu closes on Esc or a click outside it, like a native dropdown.
document.addEventListener('click', (event) => {
  if (menu.open && !menu.contains(event.target as Node)) menu.open = false;
});
menu.addEventListener('keydown', (event) => {
  if (event.key !== 'Escape' || !menu.open) return;
  menu.open = false;
  menu.querySelector('summary')?.focus();
});
window.addEventListener('beforeunload', (event) => {
  // The server keeps sessions for idleMinutes; with none, leaving ends them.
  if (idleMinutes === 0 && tabs.some((tab) => tab.live && tab.session?.open))
    event.preventDefault();
});

active = newTab('');
tabs.push(active);
paintBar();
showTabs();
showScreen();
showOffline();

/** A share link (/?share=<token>): joins the session it opens, if it is for this user. */
async function openShareLink(): Promise<void> {
  const token = new URLSearchParams(location.search).get('share');
  if (!token || demo) return;
  history.replaceState(null, '', location.pathname);
  const response = await fetch(`/api/shares/${encodeURIComponent(token)}`);
  if (!response.ok) {
    const { message } = (await response.json().catch(() => ({}))) as { message?: string };
    setStatus(message ?? 'This share link can’t be opened.', 'warn');
    return;
  }
  join({ ...((await response.json()) as SessionInfo), shareToken: token });
}

loadUser()
  .then(loadTargets)
  .then(() => {
    userFeed.start();
    // The macros' key combinations work from the start, panel open or not.
    void macros.refresh();
  })
  .then(openShareLink)
  // Not rejoining is no reason to say the targets didn't load.
  .then(() => rejoinLeft().catch(() => undefined))
  .catch((error: unknown) => {
    setStatus(`Could not load targets: ${String(error)}`, 'error');
    showOffline();
  });
