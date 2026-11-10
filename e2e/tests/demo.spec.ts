import { expect, test } from '@playwright/test';
import { connect, expectScreen, signOnFake } from './helpers.ts';

// Demo mode: the second server the e2e launcher starts (Demo__Enabled), with the fake IBM i as
// its one demo system. Visitors start as guests; sharing and administration aren't there.
test.use({ baseURL: 'http://localhost:5098' });

test.describe('demo', () => {
  test('a visitor starts the demo as a guest and reaches the menu', async ({ page }) => {
    await page.goto('/');
    await expect(page).toHaveURL(/\/demo\.html\?returnUrl=%2F/);
    await page.getByRole('button', { name: 'Start the demo' }).click();
    await expect(page).toHaveURL('http://localhost:5098/');
    await expect(page.locator('#user-name')).toHaveText(/^Guest [0-9A-F]{4}$/);
    await expect(page.locator('#terminal .offline')).toContainText(
      'takes any user name and password',
    );
    await connect(page, 'Demo IBM i');
    await signOnFake(page);
  });

  test('a guest has no sharing and no administration', async ({ page }) => {
    await page.goto('/demo.html');
    await page.getByRole('button', { name: 'Start the demo' }).click();
    await connect(page, 'Demo IBM i');
    await signOnFake(page);
    await expect(page.locator('.side-tab[data-side="sharing"]')).toBeHidden();
    await expect(page.locator('.side-tab[data-side="keys"]')).toBeVisible();
    await page.keyboard.press('Alt+S');
    await expect(page.locator('.side-tab[data-side="sharing"]')).toBeHidden();
    await expectScreen(page, /IBM i Main Menu/);

    await page.click('.bar-menu summary');
    const panel = page.locator('.bar-menu-panel');
    await expect(panel).toBeVisible();
    await expect(page.locator('#menu-share')).toBeHidden();
    await expect(page.locator('#menu-sharing')).toBeHidden();
    await expect(page.locator('#menu-admin')).toBeHidden();
  });

  test('signing out goes back to the demo page', async ({ page }) => {
    await page.goto('/demo.html');
    await page.getByRole('button', { name: 'Start the demo' }).click();
    await expect(page.locator('#user-name')).toHaveText(/^Guest /);
    await page.click('.bar-menu summary');
    await page.locator('#menu-signout').click();
    await expect(page).toHaveURL(/\/demo\.html$/);
    // Signed out: the terminal sends them back to start again.
    await page.goto('/');
    await expect(page).toHaveURL(/\/demo\.html\?returnUrl=%2F/);
  });
});
