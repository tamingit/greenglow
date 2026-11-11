# CLAUDE.md

This file guides Claude Code in this repository. It also works as a summary for people.

## Project

GreenGlow Connect ("GreenGlow" for short) is a browser TN5250 client for IBM i. It gives existing 5250 green screens a modern look without changing any RPG or DDS. A server holds each Telnet session the way IBM ACS would, and sends the browser each screen to repaint.

- **Server.** A C# server (ASP.NET Core, .NET 10) holds every TN5250 session. The browser never sees host bytes. It opens a session over HTTP, attaches over a WebSocket, gets screen snapshots as JSON and sends keys.
- **Engine.** It does the following:
  - negotiates TN5250E;
  - builds the screen model;
  - runs the keyboard model (field format word rules, type-ahead) and the read replies;
  - handles Write Error Code, Roll, Save/Restore Screen, Read Screen and Read Immediate.

  It answers a host record it can't process with a negative response, as a 5494 does.
- **Passwords.** On the server, characters typed into nondisplay fields are replaced with a placeholder in every snapshot, so no browser ever gets a password. Recordings made with `npm run capture` blank nondisplay input.
- **Sign-in.** Users sign in with Entra ID (an OIDC cookie). On the admin page (`admin.html`), administrators manage:
  - administrators;
  - connections, and who may use each one;
  - the connection log;
  - the session limits.

  A connection with no access grants is for administrators only.
- **Sessions and sharing.**
  - **Idle retention.** A session outlives the browser for a few minutes.
  - **Unexpected ends.** If a session leaves the page without the user asking, a popup says why (`ended.ts`): sign-off, the IBM i or the server lost, sharing stopped, or ended from elsewhere.
  - **Rejoining.** On load, the page rejoins the user's own sessions that no browser shows, each in a tab.
  - **Disconnected card.** It lists the user's open sessions to join, take over or end.
  - **Sharing.** An owner can share a session with named people, view-only or full access, and change or remove anyone's access at once. Shared people can view a session only while its owner is in it.
  - **Live updates.** The server pushes each page its user's session list over `/ws/user` (`UserFeed`, `userfeed.ts`). The page doesn't poll.
- **Side panels.** They pull out from tabs on the window's right edge, one at a time (`side.css`): Keys (Alt+K), Share (Alt+S, `sharing.ts`), Macros (Alt+M) and Printer (Alt+P).
- **Session tabs.** Each session has a tab in the top bar.
  - It is named after its screen's title, and coloured for its system.
  - A lock shows the link: green for verified TLS, yellow for TLS without a check, red for plain Telnet.
  - Up to 10 tabs. Alt+1 to Alt+0 switch, Alt+N or + opens a new one, and Alt+J joins a session just shared with the user.
  - The top bar holds only the logo, the tabs, who has the keyboard, the status, Full screen and the menu.
- **Data.** SQLite through EF Core holds:
  - settings and users;
  - connections, with grants and colours;
  - users' own colours;
  - the connection log;
  - shares;
  - macros (Host-on-Demand XML, with shares and key combinations; `/api/macros`);
  - the sign-in keys.

  Every key is a UUID.
- **Several servers.** Containers on different Docker hosts ("nodes") each keep their own SQLite database and sync it directly with the others (`server/src/GreenGlow.Server/Cluster/`). The settings are `Cluster__NodeId` (1–9), `Cluster__Peers__<id>` and `Cluster__Secret`.
  - Every node accepts writes. Each write gets a hybrid-logical-clock version and goes into a change journal, and the newest version wins.
  - A write pokes the peers, which then pull. The connection log is pulled every minute.
  - Peer links are sealed with the shared secret (AES-GCM, no certificates). Two adjacent peer protocol versions work together.
  - A session can't move between nodes, so a reboot is planned with a drain. The admin page's Nodes tab offers drain, resume and resync from scratch.
- **The page.** Features:
  - the Classic renderer with the ACS key map;
  - block selection, copy, and paste (the server types pasted text as the user's own keys, never Enter);
  - custom colours and theme files (`themes.ts`);
  - Copy table and Copy entire table;
  - a crosshair rule;
  - screen size modes;
  - the looks: Classic, GreenGlow and Night Shift.
- **Printer output.** It is in `printer.ts` and `server/src/GreenGlow.Server/Printing/`.
  - The user signs on to the IBM i's Network Print server with their own IBM i user and password.
  - They see their own spooled files and open one as a PDF. The server renders SCS, passes PDFs through and refuses AFP.
- **Help.** The help page (`help.html`) holds the user guide. Only administrators can open the administrator guide (`/api/admin/guide`, from `server/src/GreenGlow.Server/Help/`). `npm run screenshots` takes the screenshots against the fake IBM i.
- **Public demo.** Demo mode (`Demo__Enabled`) lets anyone sign in as a guest and use only demo systems: the fake IBM i, and PUB400 once it is enabled. See [Demo mode](#demo-mode).

## The core rule: stream-only

The client renders **only what the IBM i sends over the wire**. Nothing in the core may depend on:

- screen metadata derived from source (DSPF, PNLGRP or DDS);
- the IBM i database (DB2 queries);
- per-screen configuration files.

Screens change all the time, and test libraries differ from production, so only a stream-only client is right for whatever is deployed. Some features would need things the stream doesn't carry, such as field names, VALUES lists, the set of enabled function keys or code labels. Those features are out of scope for the core. Optional add-ons may come later, off by default, and the client must work fully without them. Don't propose a source-derived index or a database lookup as a fix.

The server's own SQLite database holds who may connect where, never anything about a screen, so it isn't screen metadata.

## Architecture

```
browser (web) ⇄ HTTPS + WebSocket /ws/session/<id> (JSON) ⇄ server (engine, one HostSession per session) ⇄ TN5250E ⇄ IBM i
   sign-in: Entra ID (OIDC cookie), or a guest in demo mode                                            tls:// verified TLS (production)
                                                                                                         tcp:// plain Telnet (test only)
```

- **Engine** (`server/src/GreenGlow.Engine`, C#): Telnet negotiation, 5250 data stream parsing, EBCDIC (CCSID 37), the screen model, the keyboard model, read replies, saved screens, recording and the scrub check.
  - `Connection` ties them together: host bytes and keys in, sends and events out.
  - It is a pure state machine with no I/O. Snapshots and events serialize with the names the page expects.
- **Server** (`server/src/GreenGlow.Server`, C#, .NET 10): holds sessions and serves the built web app, `/healthz` and the API.
  - `Sessions/`:
    - `HostSession` runs the engine on its own loop, one message at a time, and talks to the host.
    - `SessionManager` enforces the limits per node, refuses new sessions while the node drains and ends idle sessions. It also assigns device names; see below.
    - `Frontend` is one attached browser. Only the browser holding the keyboard may type, and a Full Access browser can take it.
    - `ScreenFacts` redacts nondisplay input. It reads the screen title and the IBM i user typed at sign-on, never the password.
    - `Shares` and `ShareEndpoints` handle sharing.
  - **Device names** are `GG`, the node's number, then three digits, for example `GG1002`.
    - The number is the lowest one that no session on this node holds on the same IBM i host.
    - When a session ends, its name is kept for its user for 5 minutes, for the IBM i's reconnect.
  - `Auth/`, `People/`:
    - Entra ID sign-in, the users table and administrators.
    - The guest sign-in (`GuestLogin.cs`, with `GuestPruner`).
    - `GraphDirectory` uses Microsoft Graph with the app's own identity (application permissions `User.ReadBasic.All` and `GroupMember.Read.All`).
    - `Access` says who may use which connection, with group membership cached 5 minutes.
    - With the dev login, a stand-in directory (`LocalDirectory`) is used.
  - `Admin/`: the admin API for administrators, connections and grants, settings, the connection log, and nodes (drain, resume, resync).
  - `Cluster/`:
    - `SyncRecorder`, an EF interceptor, versions and journals every write to a synced table. The journal is built generically from the EF model (`SyncModel`).
    - `SyncStore` serves the journal, snapshots and log rows, and applies a peer's.
    - `SyncService` merges peers at start, pulls when poked and on a timer, and resyncs.
    - `PeerClient` and `ClusterEndpoints` are the sealed peer link (`POST /cluster/{op}`).
    - `DbKeyRepository` keeps the sign-in key ring in the database.
    - `AgentCheck` answers HAProxy.
    - **Never delete synced rows in bulk (`ExecuteDelete`).** Delete them through the change tracker, children included, so each delete is journalled.
  - `Macros/`, `Printing/`, `Help/`: macros, printer output and the administrator guide.
  - `Data/`: EF Core with SQLite (`GreenGlowDb`) and migrations (`Data/Migrations`), run at startup.
  - `Configuration/`: options, the connection seed (`ConnectionSeed`, authoritative in demo mode), and `.env` reading in Development.
  - `Relay/`: `HostConnector` (TCP or verified TLS to the host) and `TargetCatalog`.
  - `Web/`: the Origin check, rate limiting, security headers and CSP, and static files.
  - Add a migration with `dotnet ef migrations add <Name> --project server/src/GreenGlow.Server -o Data/Migrations`. The tool is listed in `dotnet-tools.json`; run `dotnet tool restore` once first.
- **Web** (`packages/web`, TypeScript, Vite, no framework):
  - What's in it:
    - the renderer, the looks, recognizers and selection;
    - the lobby and sharing;
    - the admin page (`admin.html`, `src/admin/`);
    - the help page (`help.html`, `src/help/`);
    - the demo start page (`demo.html`, `src/demo/`).
  - Types shared with the server (snapshots, events, key names, EBCDIC display helpers) are in `src/protocol`. The page never depends on server code.
  - In development, `vite.config.ts` proxies `/api`, `/auth` and `/ws` to the server.
  - The page is installable as an app (`public/manifest.webmanifest`, `public/icons/`). It has no service worker, so nothing works offline. Keep it that way unless offline use is asked for.
  - **Brand artwork.** The sources are `brand/icon.webp` and `brand/logo.webp`. `npm run icons` renders them into `public/icons/` and `public/brand/` (`logo.png`, and `logo-dark.png` for dark backgrounds).
- **Tools**:
  - `server/tools/GreenGlow.Tools` (C#, dev only, not in the image): `capture`, `replay` and `snapshots`.
  - `tools/` (Node): `dev.ts`, `e2e.ts` and `icons.ts`.
- **Fake IBM i** (`server/tests/GreenGlow.FakeHost`):
  - A scripted TN5250E host: a sign-on screen that takes any user and password, a main menu, a three-page list and device-in-use.
  - A Network Print server (`--print-port`), with placeholder spooled files for PRTUSER / Secret12.
  - It listens on loopback unless given `--bind <address>`.
  - It is used by the server and browser tests, and by the demo container (`docker build --target fakehost`).
  - Placeholder text only.

Layout:

```
server/src/GreenGlow.Engine/    C# engine: Telnet.cs, Negotiation.cs, Record.cs, DataStream.cs, Screen.cs, Keyboard.cs,
                                Reply.cs, Saved.cs, Negative.cs, Query.cs, Recording.cs, Scrub.cs, Connection.cs
server/src/GreenGlow.Server/    the server (folders above); appsettings.json, Properties/launchSettings.json (dev profile)
server/tests/                   GreenGlow.Engine.Tests, GreenGlow.Server.Tests, GreenGlow.FakeHost (scripted fake IBM i),
                                GreenGlow.FakeHost.Tests
server/tools/GreenGlow.Tools/   capture, replay, snapshots
server/GreenGlow.slnx           the solution for all of the above
packages/web/                   Vite app (@greenglow/web): renderer.ts, text.ts, layers.ts, looks/, classic.css,
                                greenglow.css, night-shift.css, recognize/, decorate/, pointer.ts, actions.ts (sends
                                only keys a user could type), selection.ts, session.ts (REST and WebSocket), tabs.ts,
                                lobby.ts, share.ts, sharing.ts, macros.ts, printer.ts, keymap.ts, protocol/, admin/,
                                help/, demo/; brand/ (artwork sources); public/ (icons, brand, help screenshots)
e2e/                            Playwright tests (tests/: terminal, lobby, disconnect, share, admin, access, macros,
                                printer, demo, live, ...); screenshots/ takes the help page's screenshots
tools/                          dev.ts, e2e.ts, icons.ts (@greenglow/tools)
config/                         connections.example.json (copy to the gitignored connections.json)
ci/Dockerfile                   CI image: .NET 10, Node 24, Chromium
Dockerfile                      the deployable image; the fakehost stage builds the fake IBM i's image
compose.yaml                    local run of the deployable image
compose.demo.yaml               the public demo: the server in demo mode and the fake IBM i
.github/workflows/release.yml   a published GitHub release: every check, then the image to ghcr.io (beta or prod)
test/fixtures/                  scrubbed or synthetic byte-stream recordings (committed; create when needed)
test/recordings/                raw recordings (gitignored, never committed)
```

The engine is a pure state machine with no I/O, and it is not thread-safe, so each `HostSession` drives its engine from one loop. Don't add references from the engine to the server.

`tools/` runs under Node's type stripping, so it has two limits:

- It may use only erasable TypeScript: no enums, namespaces or parameter properties. `erasableSyntaxOnly` enforces this.
- It must import with `.ts` extensions.

### Configuration

Settings use ASP.NET Core names (`Section__Key`) from the environment, and in Development also from `.env`. The ones that matter:

| Setting | Purpose |
|---|---|
| `Server__PublicUrl` | The external URL. Its origin may open sessions, and share links are built on it. When it is `https://`, every request is treated as arriving there, so the Entra ID redirect is `https://` behind a TLS proxy |
| `Server__AllowedOrigins__0` | More allowed origins. The dev profile adds the Vite server |
| `Server__TrustForwardedHeaders` | Behind the TLS proxy, take the client address and scheme from `X-Forwarded-*` |
| `Server__StaticDir` | The built web app |
| `Data__DbPath` | The SQLite file (`/app/data/greenglow.db` in the container). It also holds the sign-in keys |
| `Entra__TenantId`, `Entra__ClientId`, `Entra__ClientSecret` | The Entra ID app registration, with redirect URI `<public URL>/auth/callback`. Production refuses to start without them, unless in demo mode |
| `Entra__InitialAdminUpn` | Becomes an administrator when it signs in. Add more on the admin page |
| `Auth__DevLogin` | Sign in as any UPN. Development or Testing only, and never when Entra ID is configured |
| `Demo__Enabled`, `Demo__GuestHours` (8), `Demo__GuestsPerHour` (10) | Demo mode: guest sign-in, demo systems only. See [Demo mode](#demo-mode) |
| `Seed__ConnectionsFile`, `Seed__ConnectionsJson` | Connections added at start when the database doesn't have them; later changes are made on the admin page. In demo mode the seed is the whole configuration |
| `Relay__CaFile`, `Relay__CaPem` | The CA for `tls://` connections, as a file or as PEM text. The text wins |
| `Sessions__MaxFrontends` (2), `Sessions__IdleMinutes` (5), `Sessions__MaxConnections` (50) | Defaults for the limits. A value an administrator saves wins |
| `Printer__IdleMinutes` (15) | How long, in minutes, an unused printer output sign-on lasts. `Printer__Port` and `Printer__SecurePort` point at the fake IBM i in tests, and are ignored in Production |
| `Dev__Groups` | Stand-in directory groups (members may be `*`). Not honoured in Production |
| `Cluster__NodeId` (1) | This node's number, 1 to 9, used in its device names. Give every GreenGlow that reaches the same IBM i its own number. The pipeline gives production 1 and the test instance 8; e2e uses 7 (and 6 for its demo server); the demo compose file uses 5 |
| `Cluster__Peers__<id>`, `Cluster__Secret` | The other nodes' peer URLs by number, and the secret (32+ characters) that all nodes share. With no peers, it runs as a single server |
| `Cluster__Port`, `Cluster__AgentPort` | The peer API's own port (it then answers only there), and HAProxy's agent check port |

### Demo mode

`Demo__Enabled=true` (see `compose.demo.yaml`) is a **separate guard** from the Entra ID and dev-login guards. It never relaxes them.

- **Sign-in.** `/auth/signin` goes to `/demo.html`, and its Start the demo button posts to `/auth/guest`.
  - That creates a guest user (`guest-<hex>@guest.invalid`, `IsGuest`) with a fixed-lifetime cookie and no sliding renewal.
  - The post is rate limited per client address and needs an allowed Origin.
  - `GuestPruner` removes old guests that have no session, through the change tracker.
- **Systems.** Guests see only enabled `IsDemo` connections, with no grants.
  - At each start, `ConnectionSeed.ApplyDemoAsync` makes the stored connections match the seed exactly.
  - Connections not in the seed are removed, and grants are cleared.
- **Off in demo mode.** These endpoints aren't mapped: sharing, macro sharing, the directory, the admin API and the administrator guide. The page hides their controls (`/api/me` says `demo`).
- **Refused at start.** The server won't start in demo mode with Entra ID, the dev login, `Entra__InitialAdminUpn`, or any connection that isn't `isDemo`.
- **PUB400.** `tls://pub400.com:992` is in the demo seed but `enabled: false`. Before enabling it, three things are needed:
  - its operator's agreement;
  - `Relay__CaPem` set to Sectigo's root;
  - a cap on total sessions per connection, which isn't built yet.

## Commands

You need Node 24 (see `.nvmrc`), the .NET 10 SDK and npm workspaces. `.npmrc` sets `engine-strict`, so an older Node refuses to install.

First-time setup:

```bash
npm install
cp .env.example .env                                         # then edit it (never commit .env)
cp config/connections.example.json config/connections.json   # real host names; gitignored
# certs/ibmi-ca.pem: the CA for tls:// connections (certs/ is gitignored)
```

Everyday commands:

| Command | What it does |
|---|---|
| `npm run dev` | Starts the C# server on http://localhost:5080 (dev profile: Development, dev login) and Vite on http://localhost:5173, which proxies `/api`, `/auth` and `/ws`. Open 5173. The server reads `.env`, and keeps the SQLite file in a gitignored `data/` folder |
| `npm test` | Vitest (web), then `npm run test:server` |
| `npm run test:server` | `dotnet test server/GreenGlow.slnx`: the engine, fake IBM i and server tests |
| `npm run test:watch` | Vitest in watch mode |
| `npx vitest run packages/web/test/keys.test.ts` | One web test file (`-t "name"` for one test) |
| `dotnet test server/GreenGlow.slnx --filter "FullyQualifiedName~Name"` | One server or engine test |
| `npm run typecheck` | `tsc` for the web and tools packages and for `e2e` |
| `npm run lint` | ESLint with type-aware rules |
| `npm run format` / `npm run format:check` | Prettier. Markdown is excluded and formatted by hand |
| `npm run check` | Type check, lint, format check and tests. **Run it before committing** |
| `npm run e2e` | Builds the web app and server, starts the fake IBM i, the server (dev login, port 5097) and a demo-mode server (port 5098), then runs Playwright. By default this runs in the Playwright Docker image (`E2E_DOCKER`, for example `"sudo -n docker"`); with `E2E_LOCAL_BROWSER=1` it uses a local browser. Extra arguments go to `playwright test` |
| `npm run ci` | `npm run check`, then `npm run e2e`. The pipeline runs it inside `ci/Dockerfile` |
| `npm run build` | Builds the web app into `packages/web/dist` |
| `npm run screenshots` | Takes the help page's screenshots the way `npm run e2e` runs: `packages/web/public/help/*.png` and the administrator guide's `server/src/GreenGlow.Server/Help/*.png`. Review them, then commit them |
| `npm run icons` | Renders the icons and logos from `packages/web/brand/*.webp` with Playwright's Chromium. With `ICONS_BROWSER_CHANNEL=chrome` it uses an installed Chrome instead |
| `docker compose up --build` | Builds and runs the deployable image at http://localhost:8080, or at `GREENGLOW_PORT` from `.env`. It is published on 127.0.0.1 only, and needs `.env`, `config/connections.json` and, for `tls://`, `certs/ibmi-ca.pem` |
| `docker compose -f compose.demo.yaml up --build` | The public demo at http://localhost:8080 (`DEMO_PORT`). Set `DEMO_PUBLIC_URL` and `DEMO_BEHIND_PROXY` behind a TLS proxy |
| `docker compose logs -f` / `docker compose down` | Follows the server logs / stops and removes the containers |
| `npm run capture -- test` | Records a live session on a test system up to its first screen. See the notes after this table |
| `npm run replay -- test/recordings/<file>.bin` | Replays a recording offline and prints each screen and its fields. Fails if any typed nondisplay input was recorded |
| `dotnet run --project server/tools/GreenGlow.Tools -- snapshots <file>.bin` | Writes one JSON snapshot per screen, as the server sends them. The web recordings test uses it |
| `?look=greenglow` (or `classic`, `night-shift`) | Shows the page in a look without saving the choice |

`npm run capture -- test` in more detail:

- It connects to the IBM i directly. The address comes either from the server's database or from `--url tcp://host:port`.
  - From the database (`Data__DbPath`, or `--db <path>`), the connection must be marked as a test system.
- It sends only protocol replies, never `input` or `host-read`.
- It needs `CAPTURE_DEVICE` in `.env`, and refuses targets not in `CAPTURE_TARGETS`.
- `--collision` and `--reconnect` run the device-name checks.

Browser tests and live tests:

- `e2e/tests` runs against the fake IBM i (`server/tests/GreenGlow.FakeHost`), never a real system.
- `e2e/tests/live.spec.ts` runs against a real IBM i **test** system, and only when three variables are set; otherwise it is skipped. Never point it at production.
  - The variables are `E2E_IBMI_TARGET` (`tcp://host:23` or `tls://host:992`), `E2E_IBMI_USER` and `E2E_IBMI_PASSWORD`.
  - The e2e server is node `E2E_IBMI_NODE` (default 7), so live runs reuse device names from `GG7001` up.
  - `E2E_IBMI_CA_PEM` or `E2E_IBMI_CA_FILE` gives the CA for `tls://`.
  - The connection is added as a test connection (`isTest`).
  - Traces, screenshots and video are off for these tests. The password is typed through key events inside the page, so it stays out of anything Playwright writes.
- The server's TLS tests generate throwaway certificates in memory at test time. Never commit test certificates.

Recordings and test screens:

- `RecordingsTests.cs` (in `server/tests/GreenGlow.Engine.Tests/`) replays every file in `test/recordings/`, if there are any. It fails on protocol or data stream errors.
- `packages/web/test/recordings.test.ts` runs the GreenGlow decorations over every screen of those files.
- Neither writes snapshots or prints screens, because that would copy real screens into the repo.
- Web tests build synthetic snapshots, with placeholder text only, using `packages/web/test/screens.ts`.
- Key handling is tested in C# (`KeyboardTests`).

**Sources for byte-level facts:**

- The IBM 5494 Functions Reference (SC30-3533-04; don't commit it), RFC 1205 and RFC 4777. Check them against real captures.
- tn5250 (LGPL) and tn5250j (GPL) are only for checking behaviour. Don't copy their code.

## Rules

- **Presentation only.** The client sends exactly the keys and field data the user entered. Never send keys silently on the user's behalf.
  - The only automatic sends are ones the user explicitly asked for, such as paging a subfile for Copy entire table.
  - Protocol replies that carry no user input are automatic, as on a real terminal:
    - Telnet negotiation;
    - TIMING-MARK;
    - the 5250 QUERY reply;
    - the Cancel Invite reply;
    - negative responses to host records the client can't process (a 4-byte code, no user data).
  - Host requests that return screen or field contents without an AID key (READ IMMEDIATE, READ SCREEN, SAVE SCREEN) are answered the way ACS answers them.
  - The engine's `Connection` tags every send as `negotiation`, `query-reply`, `protocol`, `host-read` or `input`. Tools that must not send user data refuse `input` and `host-read`.
- **Keyboard first, never slower than ACS.** Every mouse action has a key, no key changes meaning, and type-ahead works while input is inhibited.
- **Classic stays faithful** to what ACS shows. It is the fallback users trust.
- **Sign-in.**
  - Users sign in with Entra ID (OpenID Connect) into an HttpOnly cookie.
  - `/api` and `/ws` answer 401 without the cookie, and requests that change data must carry an allowed `Origin`.
  - `Auth__DevLogin=true` works only in Development or Testing, and only when Entra ID isn't configured.
  - Production refuses to start without Entra ID, unless in demo mode.
  - Demo mode is its own guard, refused with Entra ID, the dev login, an initial admin, or any non-demo connection.
  - Never weaken any of these guards.
- **Server security.**
  - Targets come from configuration only. The browser chooses a target by name; never accept a host or port from the client.
  - `tls://` targets verify the certificate and host name against the trusted CAs, never the system store:
    - The server's CA (`Relay__CaPem` or `Relay__CaFile`) is trusted for every connection.
    - A CA that an administrator trusted on the admin page is trusted for that one connection.
    - The chain may go through intermediates the IBM i sends.
    - A certificate for another name is trusted only after an administrator accepts that name.
    - Trusting and removing a CA are written to the connection log.
  - With no CA for a connection, `tls://` accepts any certificate. It is still encrypted but unverified, so the server warns at startup and the page and admin page say "unverified". Never weaken verification when a CA is set, and never hide that it is off.
  - `tcp://` (plain Telnet) is allowed only for test systems that don't offer TLS, and the fake IBM i. It must be chosen explicitly per target, and the server warns about it at startup. Production targets are always `tls://`.
  - No payload logging at any level. Logs carry only metadata and byte counts.
  - Enforce the Origin allowlist. Publish containers on `127.0.0.1` only when running locally.
- **Credentials and business data.** Sign-on passwords travel inside the data stream, so they are unencrypted on the network for `tcp://` targets.
  - Recordings of real sessions must blank nondisplay input fields.
    - The engine's `Connection` does this itself when it is created with `Recording`, as `npm run capture` does. `npm run replay` checks the result.
    - Never record a session any other way.
  - Recordings stay in `test/recordings/` (gitignored). Only reviewed, scrubbed or synthetic fixtures go in `test/fixtures/`.
  - For manual live sign-on tests, the user types their own credentials in their browser. Never ask the user for a password, and never type or store one yourself.
  - The one exception is the automated live tests (`e2e/tests/live.spec.ts`). They read a test-system user and password from `E2E_IBMI_USER` and `E2E_IBMI_PASSWORD`, which the user or the pipeline sets. Those credentials:
    - are for test systems only, never production;
    - are never logged, recorded or written to disk by the tests;
    - are never committed.
  - The IBM i password a user types in the Printer panel is used only to build the password substitute for the Network Print server, then dropped. It is never:
    - stored;
    - logged;
    - put in the database or the connection log;
    - sent back to a browser.
- **Nothing environment-specific in git.** No hostnames, user profiles, device names or certificates. Real values live in `.env`, `config/connections.json` and `certs/`, all gitignored. `.env.example` has placeholders only. The one named public host is PUB400 in the demo seed.
- **No company or customer names.** The project is generic. Keep employer, customer and internal system names out of code, docs, tests, screenshots and commit messages.

## Environment

- **Development.**
  - It works on Linux or macOS with the .NET 10 SDK and Node 24. Docker is needed only for the containers and for `npm run e2e`'s default browser.
  - Browsers accept plain `ws://` on `localhost`, so no local certificates are needed.
  - The Origin allowlist expects `localhost`, not `127.0.0.1`.
  - Locally you sign in with the dev login. To try Entra ID, set `Entra__*` in `.env` and register `http://localhost:5173/auth/callback` (or `:8080` for the container).
- **Deployment.**
  - Publishing a GitHub release from main runs `.github/workflows/release.yml`: every check (`npm run ci` in `ci/Dockerfile`), then the image goes to `ghcr.io/tamingit/greenglow`. A `vX.Y.Z-beta[.N]` tag is tagged `:beta`, a `vX.Y.Z` tag `:prod`, and each also gets its version. Other tags, and commits not on main, are refused.
  - Nothing deploys automatically. The server runs the image with Docker Compose, deployed by hand (README, "Releases").
  - The container runs read-only with no capabilities, on the `greenglow-data` volume. It is published on `127.0.0.1` for a TLS proxy on the same host.
- **IBM i systems.**
  - Production is reached over Telnet TLS on 992. A test system may use plain Telnet on 23.
  - Do risky experiments (reconnect, error states, disconnected jobs) on test, not production.
  - Every new device name creates a device description on the IBM i, and none are deleted. So reuse names (`CAPTURE_DEVICE`, `Cluster__NodeId`, `E2E_IBMI_NODE`) rather than inventing new ones.
- **Commits.** Commits in this repository use the author identity set in its local git config. Don't change it.
