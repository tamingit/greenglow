// npm run dev: the C# server (http://localhost:5080) and the Vite dev server
// (http://localhost:5173, which proxies /api and /ws to it), stopped together.

import { spawn, type ChildProcess } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const repoRoot = fileURLToPath(new URL('../..', import.meta.url));
const children: ChildProcess[] = [];

function start(name: string, command: string, args: string[]): void {
  const child = spawn(command, args, { cwd: repoRoot, stdio: 'inherit' });
  children.push(child);
  child.on('exit', (code) => {
    console.log(`${name} exited (${code ?? 'signal'}); stopping`);
    stop();
  });
}

let stopping = false;
function stop(): void {
  if (stopping) return;
  stopping = true;
  for (const child of children) if (child.exitCode === null) child.kill('SIGTERM');
  setTimeout(() => process.exit(0), 3000).unref();
}

process.on('SIGINT', stop);
process.on('SIGTERM', stop);

start('server', 'dotnet', [
  'run',
  '--project',
  'server/src/GreenGlow.Server',
  '--launch-profile',
  'dev',
]);
start('vite', 'npm', ['run', 'dev', '-w', '@greenglow/web']);
