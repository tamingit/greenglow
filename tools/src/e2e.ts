// npm run e2e: end-to-end tests in a real browser. Builds the web app and the server, starts
// the fake IBM i (server/tests/GreenGlow.FakeHost) and the server with the dev login on
// http://localhost:5097, then runs Playwright: in the Playwright Docker image by default
// (E2E_DOCKER, default "docker", e.g. "sudo -n docker"), or with a local browser when
// E2E_LOCAL_BROWSER=1. Extra arguments go to `playwright test`.
// `npm run screenshots` (--screenshots) runs e2e/screenshots.config.ts instead: the help
// page's screenshots.
//
// A real test system is added as the target "live" when E2E_IBMI_TARGET is set
// (tcp://host:23 or tls://host:992, with E2E_IBMI_CA_FILE or E2E_IBMI_CA_PEM for tls). E2E_IBMI_USER and
// E2E_IBMI_PASSWORD are passed to the browser tests only; nothing here prints or stores them.
// The test server is node E2E_IBMI_NODE (default 7), so its device names are GG7001 and up: every
// run reuses the same device descriptions on the IBM i, clear of other GreenGlow servers' names.
// With live credentials the HTML report is off: only the list reporter runs.

import { spawn, spawnSync, type ChildProcess } from 'node:child_process';
import { mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import * as path from 'node:path';
import { fileURLToPath } from 'node:url';

const repoRoot = fileURLToPath(new URL('../..', import.meta.url));
const PORT = 5097;
const FAKE_PORT = 2323;
/** The fake IBM i's Network Print server, for printer output. */
const FAKE_PRINT_PORT = 2324;
const BASE = `http://localhost:${PORT}`;
/** The demo-mode server (Demo__Enabled), for e2e/tests/demo.spec.ts. */
const DEMO_BASE = 'http://localhost:5098';
const children: ChildProcess[] = [];
const dataDir = mkdtempSync(path.join(tmpdir(), 'greenglow-e2e-'));

function run(command: string, args: string[]): void {
  const result = spawnSync(command, args, { cwd: repoRoot, stdio: 'inherit' });
  if (result.status !== 0) throw new Error(`${command} ${args.join(' ')} failed`);
}

/**
 * Runs a command to the end without blocking this process, which must keep reading the
 * server's and the fake IBM i's output meanwhile (spawnSync would stop that, and their
 * logging would block once the pipes filled). Returns the exit code.
 */
function runToEnd(command: string, args: string[]): Promise<number> {
  return new Promise((resolve) => {
    const child = spawn(command, args, { cwd: repoRoot, stdio: 'inherit' });
    child.on('error', () => resolve(1));
    child.on('close', (status) => resolve(status ?? 1));
  });
}

function start(name: string, command: string, args: string[], env: NodeJS.ProcessEnv): void {
  const child = spawn(command, args, { cwd: repoRoot, env, stdio: ['ignore', 'pipe', 'pipe'] });
  const prefix = (chunk: Buffer) =>
    chunk
      .toString()
      .split('\n')
      .filter(Boolean)
      .forEach((line) => console.log(`[${name}] ${line}`));
  if (process.env.E2E_VERBOSE) {
    child.stdout?.on('data', prefix);
    child.stderr?.on('data', prefix);
  } else {
    // Read and drop: a pipe nobody reads fills up, and then the server's logging blocks.
    child.stdout?.resume();
    child.stderr?.on('data', prefix);
  }
  children.push(child);
}

function stop(): void {
  for (const child of children) if (child.exitCode === null) child.kill('SIGTERM');
  rmSync(dataDir, { recursive: true, force: true });
}

async function waitFor(url: string, seconds: number): Promise<void> {
  const deadline = Date.now() + seconds * 1000;
  while (Date.now() < deadline) {
    if (children.some((child) => child.exitCode !== null))
      throw new Error('a process exited early');
    try {
      if ((await fetch(url)).ok) return;
    } catch {
      // Not up yet.
    }
    await new Promise((resolve) => setTimeout(resolve, 500));
  }
  throw new Error(`${url} did not come up in ${seconds} s`);
}

process.on('SIGINT', () => {
  stop();
  process.exit(130);
});

let code = 1;
try {
  run('npm', ['run', 'build']);
  run('dotnet', ['build', 'server/GreenGlow.slnx', '-v', 'q', '-nologo']);

  start(
    'fake-ibmi',
    'dotnet',
    [
      'run',
      '--no-build',
      '--project',
      'server/tests/GreenGlow.FakeHost',
      '--',
      '--port',
      String(FAKE_PORT),
      '--print-port',
      String(FAKE_PRINT_PORT),
    ],
    process.env,
  );

  const connections = [
    {
      name: 'fake',
      label: 'Fake IBM i',
      url: `tcp://127.0.0.1:${FAKE_PORT}`,
      isTest: true,
      // Groups by name, as a deployment's seed can give them.
      access: [{ type: 'group', name: 'Everyone' }],
    },
    {
      name: 'restricted',
      label: 'Developers only',
      url: `tcp://127.0.0.1:${FAKE_PORT}`,
      isTest: true,
      access: [{ type: 'group', name: 'Developers' }],
    },
  ];
  const live = process.env.E2E_IBMI_TARGET;
  if (live)
    connections.push({
      name: 'live',
      label: 'Live test system',
      url: live,
      isTest: true,
      access: [{ type: 'group', name: 'Everyone' }],
    });
  start(
    'server',
    'dotnet',
    ['run', '--no-build', '--no-launch-profile', '--project', 'server/src/GreenGlow.Server'],
    {
      ...process.env,
      ASPNETCORE_ENVIRONMENT: 'Testing',
      ASPNETCORE_URLS: BASE,
      Auth__DevLogin: 'true',
      Data__DbPath: path.join(dataDir, 'greenglow.db'),
      Server__PublicUrl: BASE,
      Server__StaticDir: path.join(repoRoot, 'packages', 'web', 'dist'),
      // Printer output for the fake connections goes to the fake's Network Print server.
      Printer__Port: String(FAKE_PRINT_PORT),
      Seed__ConnectionsJson: JSON.stringify({ connections }),
      Entra__InitialAdminUpn: 'admin@example.com',
      // Groups in the stand-in directory: "name=member;member".
      Dev__Groups: 'Developers=dev.user@example.com,Everyone=*',
      // Device names are GG, the node and a number: the same ones each run on a real system.
      Cluster__NodeId: process.env.E2E_IBMI_NODE ?? '7',
      ...(process.env.E2E_IBMI_CA_FILE ? { Relay__CaFile: process.env.E2E_IBMI_CA_FILE } : {}),
      ...(process.env.E2E_IBMI_CA_PEM ? { Relay__CaPem: process.env.E2E_IBMI_CA_PEM } : {}),
    },
  );
  // A second server in demo mode (e2e/tests/demo.spec.ts): guests only, the fake as its one demo system.
  start(
    'demo',
    'dotnet',
    ['run', '--no-build', '--no-launch-profile', '--project', 'server/src/GreenGlow.Server'],
    {
      ...process.env,
      ASPNETCORE_ENVIRONMENT: 'Testing',
      ASPNETCORE_URLS: DEMO_BASE,
      Data__DbPath: path.join(dataDir, 'demo.db'),
      Server__PublicUrl: DEMO_BASE,
      Server__StaticDir: path.join(repoRoot, 'packages', 'web', 'dist'),
      Printer__Port: String(FAKE_PRINT_PORT),
      Demo__Enabled: 'true',
      Seed__ConnectionsJson: JSON.stringify({
        connections: [
          {
            name: 'demo',
            label: 'Demo IBM i',
            url: `tcp://127.0.0.1:${FAKE_PORT}`,
            isDemo: true,
            isTest: true,
          },
        ],
      }),
      Cluster__NodeId: '6',
    },
  );
  await waitFor(`${BASE}/healthz`, 60);
  await waitFor(`${DEMO_BASE}/healthz`, 60);

  // --screenshots takes the help page's screenshots (e2e/screenshots.config.ts) instead.
  const screenshots = process.argv.includes('--screenshots');
  const config = screenshots ? 'e2e/screenshots.config.ts' : 'e2e';
  const extra = process.argv.slice(2).filter((arg) => arg !== '--screenshots');
  // No HTML report, traces or screenshots that could hold what the live tests typed.
  if (process.env.E2E_IBMI_PASSWORD) extra.push('--reporter=list');
  const passEnv = ['E2E_IBMI_USER', 'E2E_IBMI_PASSWORD', 'E2E_IBMI_TARGET', 'E2E_LIVE_DEBUG', 'CI'];
  const envFile = path.join(dataDir, 'browser.env');
  writeFileSync(
    envFile,
    passEnv
      .filter((name) => process.env[name])
      .map((name) => `${name}=${process.env[name]}\n`)
      .join(''),
    { mode: 0o600 },
  );
  if (process.env.E2E_LOCAL_BROWSER) {
    code = await runToEnd('npx', ['playwright', 'test', '-c', config, ...extra]);
  } else {
    const [docker, ...dockerArgs] = (process.env.E2E_DOCKER ?? 'docker').split(' ');
    const uid = process.getuid?.() ?? 1000;
    const gid = process.getgid?.() ?? 1000;
    code = await runToEnd(docker, [
      ...dockerArgs,
      'run',
      '--rm',
      '--network',
      'host',
      '--ipc',
      'host',
      '--user',
      `${uid}:${gid}`,
      '-e',
      'HOME=/tmp',
      // A private file, not "-e NAME=value": sudo drops the environment, and arguments
      // show up in the process list.
      '--env-file',
      envFile,
      '-v',
      `${repoRoot}:/work`,
      '-w',
      '/work',
      'mcr.microsoft.com/playwright:v1.63.0-noble',
      'npx',
      'playwright',
      'test',
      '-c',
      config,
      ...extra,
    ]);
  }
} catch (error) {
  console.error(String(error));
} finally {
  stop();
}
process.exit(code);
