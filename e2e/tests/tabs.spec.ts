import { expect, test, type Page } from '@playwright/test';
import { connect, expectScreen, signIn, signOnFake } from './helpers.ts';

const tabs = (page: Page) => page.getByRole('tablist', { name: 'Sessions' }).getByRole('tab');

async function openSessions(page: Page): Promise<number> {
  const response = await page.request.get('/api/sessions');
  return ((await response.json()) as { sessions: unknown[] }).sessions.length;
}

test.describe('session tabs', () => {
  test('each tab holds its own session, named after its screen', async ({ page }) => {
    // One system: + connects a new tab at once.
    await signIn(page, 'tabs.two@example.com');
    await expect(tabs(page)).toHaveText(['New session']);
    await connect(page, 'Fake IBM i');
    await expect(tabs(page)).toHaveText(['Sign On']);
    await signOnFake(page);
    await expect(tabs(page)).toHaveText(['IBM i Main Menu']);

    await page.getByRole('button', { name: 'New session', exact: true }).click();
    await expect(tabs(page)).toHaveText(['IBM i Main Menu', 'Sign On']);
    await expect(tabs(page).nth(1)).toHaveAttribute('aria-selected', 'true');
    await expectScreen(page, /Sign On/);
    expect(await openSessions(page)).toBe(2);

    // Alt+1 and Alt+2 go between them, and keys go to the tab showing.
    await page.keyboard.press('Alt+1');
    await expect(tabs(page).nth(0)).toHaveAttribute('aria-selected', 'true');
    await expectScreen(page, /IBM i Main Menu/);
    await page.keyboard.press('Alt+2');
    await expectScreen(page, /Sign On/);
    // Its status no longer says Connecting once its screen shows.
    await expect(page.locator('#status-text')).not.toContainText('Connecting');
    await signOnFake(page);
    await expect(tabs(page)).toHaveText(['IBM i Main Menu', 'IBM i Main Menu']);

    // Closing a signed-on tab asks first, as Disconnect does.
    await page.getByRole('button', { name: 'Close IBM i Main Menu' }).nth(1).click();
    const dialog = page.getByRole('alertdialog');
    await expect(dialog).toContainText('Close Fake IBM i?');
    await dialog.getByRole('button', { name: 'End session' }).click();
    await expect(tabs(page)).toHaveCount(1);
    await expectScreen(page, /IBM i Main Menu/);
    await expect.poll(() => openSessions(page)).toBe(1);

    // Left running, the last tab's session stays on the server; a new tab stands in.
    await page.getByRole('button', { name: 'Close IBM i Main Menu' }).click();
    await page.getByRole('alertdialog').getByRole('button', { name: 'Leave it running' }).click();
    await expect(tabs(page)).toHaveText(['New session']);
    await expect(page.getByRole('table', { name: 'Your open session' })).toBeVisible();
    expect(await openSessions(page)).toBe(1);
  });

  test('a tab closes at once before sign-on', async ({ page }) => {
    await signIn(page, 'tabs.close@example.com');
    await connect(page, 'Fake IBM i');
    await page.keyboard.press('Alt+N');
    await expect(tabs(page)).toHaveCount(2);
    await expectScreen(page, /Sign On/);
    await tabs(page).nth(1).focus();
    await page.keyboard.press('Delete');
    await expect(tabs(page)).toHaveCount(1);
    await expect.poll(() => openSessions(page)).toBe(1);
  });

  test('a session past the connection’s limit is refused in a popup', async ({ page }) => {
    // One system: each new tab connects at once. The fake IBM i allows three per user.
    await signIn(page, 'tabs.limit@example.com');
    await connect(page, 'Fake IBM i');
    await page.keyboard.press('Alt+N');
    await expectScreen(page, /Sign On/);
    await page.keyboard.press('Alt+N');
    await expectScreen(page, /Sign On/);
    await expect.poll(() => openSessions(page)).toBe(3);
    await page.keyboard.press('Alt+N');
    const notice = page.getByRole('alertdialog', { name: 'Session limit reached' });
    await expect(notice).toContainText('You already have 3 sessions on Fake IBM i');
    await expect(notice.getByRole('button', { name: 'OK' })).toBeFocused();
    await page.keyboard.press('Escape');
    await expect(notice).toHaveCount(0);
    expect(await openSessions(page)).toBe(3);
  });

  test('a new tab lists the sessions open in another window instead of connecting', async ({
    page,
    context,
  }) => {
    await signIn(page, 'tabs.elsewhere@example.com');
    await connect(page, 'Fake IBM i');
    const other = await context.newPage();
    await other.goto('/');
    await connect(other, 'Fake IBM i');
    await expect.poll(() => openSessions(other)).toBe(2);

    await other.keyboard.press('Alt+N');
    await expect(tabs(other)).toHaveCount(2);
    const row = other.getByRole('table', { name: 'Your open session' }).getByRole('row').nth(1);
    await expect(row).toContainText('1 of 2');
    expect(await openSessions(other)).toBe(2);
    await row.getByRole('button', { name: 'Join' }).click();
    await expectScreen(other, /Sign On/);
    expect(await openSessions(other)).toBe(2);
  });

  test('there are at most ten tabs', async ({ page }) => {
    // Two systems: a new tab waits for a choice, so nothing connects here.
    await signIn(page, 'dev.user@example.com');
    await expect(page.getByRole('group', { name: 'Systems' })).toBeVisible();
    for (let i = 0; i < 9; i++) await page.keyboard.press('Alt+N');
    await expect(tabs(page)).toHaveCount(10);
    await expect(page.getByRole('button', { name: 'New session', exact: true })).toBeDisabled();
    await page.keyboard.press('Alt+N');
    await expect(tabs(page)).toHaveCount(10);
    const notice = page.getByRole('alertdialog', { name: 'Session limit reached' });
    await expect(notice).toContainText('At most 10 sessions');
    await page.keyboard.press('Enter');
    await expect(notice).toHaveCount(0);
    await page.keyboard.press('Alt+0');
    await expect(tabs(page).nth(9)).toHaveAttribute('aria-selected', 'true');
  });

  test('several sessions left running can be joined at once, a tab each', async ({ page }) => {
    await signIn(page, 'tabs.join@example.com');
    // Once the page has looked for sessions to join, so these two are listed instead.
    await page.waitForLoadState('networkidle');
    const origin = new URL(page.url()).origin;
    for (let i = 0; i < 2; i++) {
      const opened = await page.request.post('/api/sessions', {
        data: { target: 'fake' },
        headers: { origin },
      });
      expect(opened.ok()).toBe(true);
    }
    // The server pushes the list (/ws/user).
    const table = page.getByRole('table', { name: 'Your open sessions' });
    await expect(table.getByRole('row')).toHaveCount(4);
    const join = table.getByRole('button', { name: /^Join .*selected$/ });
    await expect(join).toBeDisabled();
    await table.getByRole('checkbox', { name: 'Select all sessions' }).check();
    await expect(join).toHaveText('Join 2 selected');
    await join.click();
    await expect(tabs(page)).toHaveCount(2);
    await expect(tabs(page)).toHaveText(['Sign On', 'Sign On']);
    await expectScreen(page, /Sign On/);
    await page.keyboard.press('Alt+2');
    await expectScreen(page, /Sign On/);
  });

  test('sessions no browser shows are joined when the page loads, a tab each', async ({ page }) => {
    await signIn(page, 'tabs.load@example.com');
    const origin = new URL(page.url()).origin;
    for (let i = 0; i < 2; i++) {
      const opened = await page.request.post('/api/sessions', {
        data: { target: 'fake' },
        headers: { origin },
      });
      expect(opened.ok()).toBe(true);
    }
    await page.reload();
    await expect(tabs(page)).toHaveText(['Sign On', 'Sign On']);
    await expect(tabs(page).first()).toHaveAttribute('aria-selected', 'true');
    await expectScreen(page, /Sign On/);
  });
});
