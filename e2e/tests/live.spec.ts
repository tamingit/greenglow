// Tests against a real IBM i test system. They run only when E2E_IBMI_TARGET, E2E_IBMI_USER
// and E2E_IBMI_PASSWORD are set (npm run e2e adds the target as "Live test system"), and
// skip otherwise. Use a test system only, never production.
//
// The password must never land in a trace, screenshot, video or report: tracing is off here,
// and the password is typed by dispatching key events from inside the page, so it never
// appears in Playwright's own logs.

import { expect, test, type Page } from '@playwright/test';
import { cell, disconnect, expectScreen, screenText, signIn, type } from './helpers.ts';

const target = process.env.E2E_IBMI_TARGET;
const user = process.env.E2E_IBMI_USER;
const password = process.env.E2E_IBMI_PASSWORD;
const LIVE_UPN = 'live.tester@example.com';

test.skip(
  !target || !user || !password,
  'Set E2E_IBMI_TARGET, E2E_IBMI_USER and E2E_IBMI_PASSWORD to test a real system.',
);
// E2E_LIVE_DEBUG keeps traces and screenshots: only for a dry run against the fake IBM i
// with a made-up password, never with real credentials.
test.use(process.env.E2E_LIVE_DEBUG ? {} : { trace: 'off', screenshot: 'off', video: 'off' });
test.describe.configure({ mode: 'serial' });

/** Types text as key events dispatched in the page; the text isn't in Playwright's logs. */
async function typeSecret(page: Page, secret: string): Promise<void> {
  await page.locator('#terminal').evaluate((terminal, text) => {
    for (const key of text) {
      terminal.dispatchEvent(
        new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true }),
      );
    }
  }, secret);
}

/** Signs on with the configured user, wherever the cursor is on the sign-on screen. */
async function signOnLive(page: Page): Promise<void> {
  await page
    .locator('#terminal .offline')
    .getByRole('button', { name: /^(Connect to )?Live test system/ })
    .first()
    .click();
  await expect(page.locator('#terminal .offline')).toBeHidden({ timeout: 30_000 });
  await expectScreen(page, /\S/);
  await type(page, user!);
  await page.keyboard.press('Tab');
  await typeSecret(page, password!);
  await page.keyboard.press('Enter');
  // Past sign-on: the screen's input fields change. The session list says so.
  await expect
    .poll(
      async () => {
        const response = await page.request.get('/api/sessions');
        const { sessions } = (await response.json()) as {
          sessions: { signedOn: boolean; ibmUser: string | null }[];
        };
        return sessions.find((s) => s.signedOn)?.ibmUser ?? null;
      },
      { timeout: 30_000 },
    )
    .toBe(user!.toUpperCase());
}

/** Signs off with the SIGNOFF command on the command line; the IBM i then ends the session. */
async function signOff(page: Page): Promise<void> {
  await type(page, 'SIGNOFF');
  await page.keyboard.press('Enter');
  await expect(page.locator('#terminal .offline')).toBeVisible({ timeout: 30_000 });
  await expect(page.locator('#status-text')).toHaveText('Session ended.');
  // The notice says why; OK puts it away.
  await expect(page.getByRole('alertdialog', { name: 'Session ended' })).toBeVisible();
  await page.keyboard.press('Enter');
}

test.describe('live test system', () => {
  test('signs on, shows the user and screen in the session list, and signs off', async ({
    page,
  }) => {
    test.setTimeout(90_000);
    await signIn(page, LIVE_UPN);
    await signOnLive(page);
    expect(await screenText(page)).not.toContain(password!);

    // Leave it running and see it in the list with the user and a screen title.
    await disconnect(page);
    await page.getByRole('alertdialog').getByRole('button', { name: 'Leave it running' }).click();
    const row = page
      .getByRole('table', { name: /Your open session/ })
      .getByRole('row')
      .nth(1);
    await expect(row).toContainText(user!.toUpperCase());
    await expect(row).toContainText('Live test system');
    await row.getByRole('button', { name: 'Join' }).click();
    await expect(page.locator('#terminal .offline')).toBeHidden();

    // A block selection from the real screen copies.
    await page.context().grantPermissions(['clipboard-read', 'clipboard-write']);
    const from = await cell(page, 1, 1);
    const to = await cell(page, 2, 20);
    await page.mouse.move(from.x, from.y);
    await page.mouse.down();
    await page.mouse.move(to.x, to.y, { steps: 5 });
    await page.mouse.up();
    await page.keyboard.press('Control+c');
    const copied = await page.evaluate(() => navigator.clipboard.readText());
    expect(copied.split('\n')).toHaveLength(2);

    // Typing clears the selection; the cursor is still on the command line.
    await signOff(page);
  });

  test('a view-only share of a live session shows its screens', async ({ page, browser }) => {
    test.setTimeout(90_000);
    await signIn(page, LIVE_UPN);
    await signOnLive(page);
    await page.click('.bar-menu summary');
    await page.click('#menu-share');
    const dialog = page.getByRole('dialog', { name: /Share your session/ });
    await dialog.getByLabel('Add a person').fill('live.viewer@example.com');
    await page.getByRole('option', { name: /live\.viewer@example\.com/ }).click();
    await dialog.getByRole('button', { name: 'Create link' }).click();
    const link = await dialog.getByLabel('Share link').inputValue();
    await page.keyboard.press('Escape');

    const viewer = await (await browser.newContext()).newPage();
    await signIn(viewer, 'live.viewer@example.com');
    await viewer.goto(link);
    await expect(viewer.locator('#control-text')).toHaveText('View only');
    const owners = (await screenText(page)).trim();
    await expect.poll(async () => (await screenText(viewer)).trim()).toBe(owners);
    expect(await screenText(viewer)).not.toContain(password!);

    await signOff(page);
    await expect(viewer.locator('#terminal .offline')).toBeVisible({ timeout: 30_000 });
  });
});
