import { expect, test, type Locator, type Page } from '@playwright/test';
import { connect, endSessions, signIn } from './helpers.ts';

/** The initial administrator, from Entra__InitialAdminUpn in the e2e server. */
const ADMIN = 'admin@example.com';

function systemColor(node: Locator): Promise<string> {
  return node.evaluate((element) =>
    getComputedStyle(element).getPropertyValue('--system-color').trim(),
  );
}

async function setAdminColor(page: Page, color: string | null): Promise<void> {
  await page.goto('/admin.html#connections');
  const card = page.locator('.admin-card[data-connection="restricted"]');
  const enabled = card.getByRole('checkbox', { name: 'Colour' });
  if (color) {
    await enabled.check();
    await card.getByLabel('Choose the colour').fill(color);
  } else {
    await enabled.uncheck();
  }
  await card.getByRole('button', { name: 'Save' }).click();
  await expect(page.locator('#admin-message')).toContainText('Saved Developers only');
}

test.describe('system colours', () => {
  test('an administrator colours a system, and a user chooses their own', async ({ browser }) => {
    const admin = await (await browser.newContext()).newPage();
    await signIn(admin, ADMIN);
    await setAdminColor(admin, '#c98a1b');

    const page = await (await browser.newContext()).newPage();
    await signIn(page, 'dev.user@example.com');
    const tile = page
      .getByRole('group', { name: 'Systems' })
      .getByRole('button', { name: /^Developers only/ });
    await expect(tile).toHaveAttribute('data-colored');
    expect(await systemColor(tile)).toBe('#c98a1b');

    await connect(page, 'Developers only');
    const bar = page.locator('.app-bar');
    await expect(bar).toHaveAttribute('data-colored');
    expect(await systemColor(bar)).toBe('#c98a1b');
    // The tab takes the colour of its system.
    const tab = page.locator('.tab-item').first();
    expect(await systemColor(tab)).toBe('#c98a1b');

    await page.click('.bar-menu summary');
    await expect(page.locator('#color-label')).toHaveText('Colour for Developers only');
    await page.locator('#my-color').fill('#3366ff');
    await expect(page.locator('#color-reset')).toBeVisible();
    await expect.poll(() => systemColor(bar)).toBe('#3366ff');

    // Kept on the server: the same after a reload, which joins the session again.
    await page.reload();
    await expect.poll(() => systemColor(bar)).toBe('#3366ff');

    await page.click('.bar-menu summary');
    await page.click('#color-reset');
    await expect.poll(() => systemColor(bar)).toBe('#c98a1b');
    await expect(page.locator('#color-reset')).toBeHidden();

    await setAdminColor(admin, null);
    await page.reload();
    await expect(page.locator('.tab-item').first()).toHaveAttribute('data-connected');
    await expect(bar).not.toHaveAttribute('data-colored');
    // Others sign in as this user; they would join this session on loading.
    await endSessions(page);
    // Its own contexts: closed here, or they keep running for the rest of the run.
    await admin.context().close();
    await page.context().close();
  });
});
