# GreenGlow Connect

A browser-based TN5250 client for IBM i. It gives the existing 5250 green screens a modern look (themes, clickable function keys, table-style subfiles, notifications) without changing any RPG or DDS. It connects over Telnet the way IBM ACS does and draws each screen only from what the IBM i sends.

**Status: Phase 0 (spike), with sign-in, sessions and administration built.** A C# server holds the Telnet sessions. Users sign in with Entra ID, and administrators decide who may use which IBM i system. The engine negotiates TN5250E, builds the screen model, handles keys and read replies, and answers host records it can't process with a negative response, as a 5494 does. The page has the Classic look with the ACS key map, plus GreenGlow and Night Shift looks. The first live sign-on through the server and the reconnect test are next. It isn't usable for real work yet, so keep using ACS.

## How it works

```
browser ⇄ HTTPS and WebSocket (JSON) ⇄ server container (engine, sessions, SQLite) ⇄ Telnet ⇄ IBM i
```

- **`server/src/GreenGlow.Engine`**: Telnet, the 5250 data stream, EBCDIC, and the screen and keyboard models, in C#. It does no I/O.
- **`server/src/GreenGlow.Server`**: an ASP.NET Core server (.NET 10) in a Docker container. Browsers can't open raw TCP connections, so the server holds each TN5250 session itself. A browser opens a session, attaches to it over a WebSocket, receives screen snapshots as JSON and sends keys. It never sees host bytes, and characters typed into password fields are replaced with a placeholder before a snapshot leaves the server. The server connects only to the systems in its database, verifies TLS certificates, and never logs screen data or keystrokes. It also serves the web app and keeps users, connections, settings, shares and the connection log in SQLite.
- **`packages/web`**: the page. It turns snapshots into screens (Classic, GreenGlow and Night Shift looks), and has the admin page and the session list.

What users get:

- **Sign-in** with Entra ID. Administrators are set on the admin page; the first comes from `Entra__InitialAdminUpn`.
- **Connections.** Each IBM i system has a name, label, address (`tls://` or `tcp://`), a per-user session limit and the Entra users and groups allowed to use it. A connection with no grants is for administrators only.
- **Sessions** stay open on the server for a few minutes after the last browser leaves, so a reload or closed tab can return to the same IBM i job. The Disconnected card lists a user's open sessions to join or end.
- **Sharing.** The owner of a session can share it with named people, view-only or with full access (they can take control and type under the owner's IBM i user). Links work only for the people named.
- **Administration** (`admin.html`): administrators, connections and access, the connection log (with CSV), and the session limits.

## Getting started

You need:

- Windows with WSL2 (Ubuntu) and VS Code, or Linux or macOS
- The .NET 10 SDK
- Node 24 (install with [nvm](https://github.com/nvm-sh/nvm), which reads `.nvmrc`)
- Docker Desktop with WSL integration, to run the container and the browser tests

Clone inside the WSL filesystem (for example `~/repos`), not under `/mnt/c`, so file watching stays fast.

```bash
git clone <repository URL> greenglow-connect
cd greenglow-connect
nvm install
npm install
cp .env.example .env
cp config/connections.example.json config/connections.json
```

Then:

1. **Edit `config/connections.json`** and list the IBM i systems you want, using their real host names. `tls://` is verified TLS for production. `tcp://` is plain Telnet, only for the test system, which has no TLS. The file seeds the database on first start; after that, administrators manage connections on the admin page.
2. **Add the CA certificate** for `tls://` connections as `certs/ibmi-ca.pem`. It's the root CA your IBM i certificate chains to (often an internal CA your Windows PC already trusts). Export it as a Base-64 (PEM) file.

`.env`, `config/connections.json` and `certs/` are gitignored. Never commit host names, certificates or session recordings.

## Running it

| Command | What it does |
|---|---|
| `npm run dev` | The server and the Vite dev server with hot reload: open http://localhost:5173. Sign in with the dev login (any UPN) |
| `docker compose up --build` | The deployable container: http://localhost:8080 |
| `npm test` | Vitest (web) and `dotnet test` (engine, fake IBM i, server) |
| `npm run e2e` | Playwright browser tests against a scripted fake IBM i |
| `npm run check` | Type check, lint, format check and tests. Run it before committing |
| `npm run capture -- test` | Record a live session on a test system up to its first screen (protocol replies only) |
| `npm run replay -- test/recordings/<file>.bin` | Replay a recording offline and print each screen |

Open the page, sign in, pick a system and choose **Connect**. The page shows the sign-on screen and you can type into it with the ACS keys. Type your own credentials in your browser; recordings blank nondisplay input. Automated live tests against a test system read credentials from `E2E_IBMI_*` variables and are skipped without them.

The full command list is in [CLAUDE.md](CLAUDE.md), which also holds the project rules for Claude Code and doubles as a summary for humans.

## Configuration

Settings use ASP.NET Core names (`Section__Key`) in the environment, and in development in `.env`; [.env.example](.env.example) lists them. The main ones:

- `Server__PublicUrl`: the external URL, which share links are built on. Register `<URL>/auth/callback` in Entra ID as the redirect URI.
- `Entra__TenantId`, `Entra__ClientId`, `Entra__ClientSecret`, `Entra__InitialAdminUpn`: sign-in. The app registration needs the Microsoft Graph application permissions `User.ReadBasic.All` and `GroupMember.Read.All`, with admin consent, to search people and groups. Production refuses to start without Entra ID.
- `Auth__DevLogin`: sign in as any UPN. Development and tests only, and never with Entra ID configured.
- `Data__DbPath`: the SQLite file. Keep it, and the `keys` folder beside it, on a volume.
- `Seed__ConnectionsFile` or `Seed__ConnectionsJson`: the connections to add on first start.
- `Relay__CaFile` or `Relay__CaPem`: the CA for `tls://` connections.
- `Sessions__MaxFrontends` (browsers per session, default 2), `Sessions__IdleMinutes` (default 5) and `Sessions__MaxConnections` (default 50): defaults that administrators can change on the admin page.

## Releases

Publishing a GitHub release from main builds the server image and pushes it to GitHub's container registry. The work is done by [.github/workflows/release.yml](.github/workflows/release.yml). Nothing is deployed: you do that by hand on the server.

```bash
gh release create v1.2.0-beta.1 --target main --prerelease --generate-notes   # beta
gh release create v1.2.0 --target main --generate-notes                       # prod
```

| Release tag | Image tags |
|---|---|
| `vX.Y.Z-beta` or `vX.Y.Z-beta.N` | `ghcr.io/tamingit/greenglow:X.Y.Z-beta.N` and `:beta` |
| `vX.Y.Z` | `ghcr.io/tamingit/greenglow:X.Y.Z` and `:prod` |

- **Checks first:** every check in `ci/Dockerfile` (`npm run ci`, browser tests included) must pass before anything is pushed.
- **Refused:** any other tag, and any release whose commit isn't on main. Nothing is built for them.
- **Kept:** each version keeps its own image tag, so you can go back to one.

### Running a release

On the server, make a folder for each instance (say `~/greenglow-prod`) holding three things:

- **`compose.yml`:** the example below.
- **`greenglow.env`:** the settings.
- **`config/connections.json` and `certs/ibmi-ca.pem`:** as in [Getting started](#getting-started).

```yaml
# ~/greenglow-prod/compose.yml
# Deploy or update:  docker compose pull && docker compose up -d
services:
  greenglow:
    # :prod follows the latest prod release; :beta the latest beta. A version (:1.2.0) stays put.
    image: ghcr.io/tamingit/greenglow:prod
    container_name: greenglow-prod
    restart: unless-stopped
    init: true
    env_file: greenglow.env
    environment:
      # Paths inside the container.
      Seed__ConnectionsFile: /app/config/connections.json
      Relay__CaFile: /app/certs/ibmi-ca.pem
      Data__DbPath: /app/data/greenglow.db
      # Behind the TLS proxy on this host.
      Server__TrustForwardedHeaders: 'true'
    ports:
      # Localhost only, for the TLS proxy. Published on all interfaces, anyone on the network
      # could reach the IBM i through this server.
      - '127.0.0.1:5090:8080'
    volumes:
      - ./config:/app/config:ro
      - ./certs:/app/certs:ro
      # The SQLite database and the sign-in keys: back it up.
      - greenglow-data:/app/data
    read_only: true
    tmpfs:
      - /tmp
    cap_drop: [ALL]
    security_opt: ['no-new-privileges:true']

volumes:
  greenglow-data:
```

```bash
# ~/greenglow-prod/greenglow.env (never commit it)
Server__PublicUrl=https://greenglow.example.com
Entra__TenantId=00000000-0000-0000-0000-000000000000
Entra__ClientId=00000000-0000-0000-0000-000000000000
Entra__ClientSecret=
Entra__InitialAdminUpn=someone@example.com
# This server's node number, 1 to 9, unique among every GreenGlow reaching the same IBM i.
Cluster__NodeId=1
```

- **Beta:** copy the folder to `~/greenglow-beta` and change four things:
  - the image to `:beta`
  - `container_name` to `greenglow-beta`
  - the port to `127.0.0.1:5091:8080`
  - `Cluster__NodeId` to 8

  Give beta its own Entra redirect URI (`<beta URL>/auth/callback`). Each folder gets its own volume, because Compose names volumes after the folder.
- **Going back:** set `image:` to an earlier version, such as `:1.1.0`, and run `docker compose up -d`.
- **Health:** the image has a health check; `docker compose ps` shows it.
- **Registry sign-in:** if the `greenglow` package on GitHub is private, sign the server in once with `docker login ghcr.io`, using a token that has `read:packages`.

## Public demo

[compose.demo.yaml](compose.demo.yaml) runs a public demo: GreenGlow Connect in demo mode, with the fake IBM i (placeholder screens) in a container beside it.

```bash
docker compose -f compose.demo.yaml up --build
```

Open http://localhost:8080 (or `DEMO_PORT`). Behind a TLS proxy, set `DEMO_PUBLIC_URL` to the public `https://` address and `DEMO_BEHIND_PROXY=true`. The marketing site's "Try it" link goes to `<public URL>/demo.html`.

In demo mode (`Demo__Enabled=true`):

- **Sign-in:** visitors press Start the demo and become a guest, with no account. A guest's sign-in lasts `Demo__GuestHours` (8). A client address may start `Demo__GuestsPerHour` guests (10). Guests not seen for twice that time who have no session open are removed every hour.
- **Systems:** guests may use only demo systems (`"isDemo": true` in the seed). The seed is the whole configuration: at each start, connections are made to match it, and ones not in it are removed. A seed entry takes no access list, because every guest may use every enabled demo system.
- **What's off:** sharing, the people and group directory, and the admin page. Macros, colours, the looks and printer output work. The fake's print user is PRTUSER, password Secret12.
- **Refused at start:** the server won't start in demo mode with Entra ID, the dev login, `Entra__InitialAdminUpn`, or any connection that isn't a demo system. Demo mode never relaxes the normal sign-in rules.

PUB400 (`tls://pub400.com:992`, a free public IBM i whose users make their own account at pub400.com) is in the demo seed but disabled. Before you enable it:

- Its operator has to agree, because its rules say no commercial environments, and it limits connections from one address. Every demo visitor connects from the server's address.
- Set `Relay__CaPem` to Sectigo's root, which its certificate chains to.
- A cap on sessions in total for that connection still has to be added to the server.

Then set `"enabled": true` in `compose.demo.yaml` and restart.

## Ground rules

- **Stream-only.** The client uses only what the IBM i sends. It doesn't read DDS, the application source or DB2, so it stays correct for whatever is deployed.
- **Presentation only.** It sends exactly what the user typed, and never presses keys on their behalf.
- **Never slower than ACS.** Keyboard first, and type-ahead works while the keyboard is locked.
- **Security.** Everyone signs in. Production connections use TLS, verified against your CA when `IBMI_CA_PEM` is set (without it the certificate isn't checked, and the page says so), and a connection nobody has been granted is for administrators only. Locally the container listens only on `localhost`. The server never logs screen data or keystrokes, and never sends a typed password to any browser. Sign-on passwords travel inside the Telnet stream, so real session recordings stay out of git.
