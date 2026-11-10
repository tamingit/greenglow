// `npm run screenshots`: the help page's screenshots, taken from the built app, the server and
// the fake IBM i (placeholder screens only), as `npm run e2e` runs them. It writes
// packages/web/public/help/*.png (the user guide) and server/src/GreenGlow.Server/Help/*.png
// (the administrator guide); review them, then commit them.

import { defineConfig, devices } from '@playwright/test';

export default defineConfig({
  testDir: './screenshots',
  fullyParallel: false,
  workers: 1,
  timeout: 120_000,
  expect: { timeout: 10_000 },
  reporter: [['list']],
  outputDir: '../test-results/screenshots',
  use: {
    baseURL: process.env.E2E_BASE_URL ?? 'http://localhost:5097',
    trace: 'off',
    screenshot: 'off',
  },
  projects: [
    {
      name: 'chromium',
      use: {
        ...devices['Desktop Chrome'],
        viewport: { width: 1280, height: 820 },
        deviceScaleFactor: 1,
      },
    },
  ],
});
