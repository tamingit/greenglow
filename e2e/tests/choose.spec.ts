import { expect, test } from '@playwright/test';
import { disconnect, expectScreen, signIn } from './helpers.ts';

test.describe('choosing a system', () => {
  test('a user with one system gets a button for it, not a choice', async ({ page }) => {
    await signIn(page, 'choose.one@example.com');
    const card = page.locator('#terminal .offline');
    await expect(card.getByRole('button', { name: 'Connect to Fake IBM i' })).toBeVisible();
    await expect(card.getByRole('group', { name: 'Systems' })).toBeHidden();
  });

  test('a user with several chooses one from the keyboard, and it is chosen again next time', async ({
    page,
  }) => {
    await signIn(page, 'dev.user@example.com');
    const card = page.locator('#terminal .offline');
    const systems = card.getByRole('group', { name: 'Systems' });
    await expect(card.getByText('Choose a system')).toBeVisible();
    await expect(systems.getByRole('button')).toHaveCount(2);
    await expect(card.locator('.offline-action')).toBeHidden();
    const first = systems.getByRole('button', { name: /^Fake IBM i/ });
    const second = systems.getByRole('button', { name: /^Developers only/ });
    await expect(second).toContainText('Not encrypted');
    await expect(first).toBeFocused();

    await page.keyboard.press('ArrowDown');
    await expect(second).toBeFocused();
    await page.keyboard.press('Enter');
    await expect(card).toBeHidden();
    await expectScreen(page, /Sign On/);
    // The tab names its system, and its lock says plain Telnet: red.
    const tab = page.getByRole('tablist', { name: 'Sessions' }).getByRole('tab').first();
    await expect(tab).toHaveAttribute('title', /Developers only/);
    const lock = page.locator('.tab-lock').first();
    await expect(lock).toHaveAttribute('data-security', 'plain');
    await expect(lock).toHaveAccessibleName('Not encrypted');

    // Before sign-on, Disconnect ends the session at once.
    await disconnect(page);
    await expect(second).toBeFocused();
    await page.reload();
    await expect(second).toBeFocused();
    await expect(second).toHaveClass(/chosen/);
  });
});
