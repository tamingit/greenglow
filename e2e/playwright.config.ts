// End-to-end tests: a real browser against the built app, the C# server and the fake IBM i
// (server/tests/GreenGlow.FakeHost). Run with `npm run e2e`, which starts both and runs
// these in the Playwright Docker image (or locally with E2E_LOCAL_BROWSER=1).
// Tests against a real test system run only when E2E_IBMI_* are set; see live.spec.ts.

import { defineConfig, devices } from '@playwright/test';

export default defineConfig({
  testDir: './tests',
  fullyParallel: false,
  workers: 1,
  timeout: 30_000,
  expect: { timeout: 10_000 },
  retries: process.env.CI ? 1 : 0,
  reporter: [['list'], ['html', { open: 'never', outputFolder: '../test-results/e2e-report' }]],
  outputDir: '../test-results/e2e',
  use: {
    baseURL: process.env.E2E_BASE_URL ?? 'http://localhost:5097',
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    viewport: { width: 1400, height: 900 },
  },
  projects: [
    {
      name: 'chromium',
      use: { ...devices['Desktop Chrome'], viewport: { width: 1400, height: 900 } },
    },
  ],
});
