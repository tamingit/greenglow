import { expect, test } from '@playwright/test';
import { ADMIN, signIn } from './helpers.ts';

test.describe('help', () => {
  test('Help in the menu opens the user guide in a new tab, with its pictures', async ({
    page,
  }) => {
    await signIn(page, 'help.reader@example.com');
    await page.click('.bar-menu summary');
    const opening = page.context().waitForEvent('page');
    await page.click('#menu-help');
    const help = await opening;
    await help.waitForLoadState();
    await expect(help).toHaveURL(/\/help\.html$/);
    await expect(help.getByRole('heading', { name: 'GreenGlow Connect user guide' })).toBeVisible();
    await expect(page.locator('.bar-menu-panel')).toBeHidden();

    // The contents list follows the sections, and every picture loads.
    const contents = help.getByRole('navigation', { name: 'Contents' });
    await expect(contents.getByRole('link', { name: 'Sharing a session' })).toBeVisible();
    await help.locator('#guide-user img').last().scrollIntoViewIfNeeded();
    await expect
      .poll(() =>
        help
          .locator('#guide-user img')
          .evaluateAll(
            (images) =>
              (images as HTMLImageElement[]).filter((image) => image.naturalWidth === 0).length,
          ),
      )
      .toBe(0);

    // Not an administrator: no administrator guide, even at its address.
    await expect(help.getByRole('navigation', { name: 'Guides' })).toBeHidden();
    await help.goto('/help.html#admin');
    await expect(help.locator('#guide-admin')).toBeHidden();
    await expect(help.locator('#guide-user')).toBeVisible();
    expect((await help.request.get('/api/admin/guide')).status()).toBe(403);
  });

  test('an administrator also has the administrator guide', async ({ page }) => {
    await signIn(page, ADMIN, '/admin.html');
    const opening = page.context().waitForEvent('page');
    await page.getByRole('link', { name: 'Help' }).click();
    const help = await opening;
    await expect(help).toHaveURL(/\/help\.html#admin$/);
    const guide = help.locator('#guide-admin');
    await expect(
      guide.getByRole('heading', { name: 'GreenGlow Connect administrator guide' }),
    ).toBeVisible();
    await expect(help.locator('#guide-user')).toBeHidden();
    await expect
      .poll(() =>
        guide
          .locator('img')
          .evaluateAll(
            (images) =>
              (images as HTMLImageElement[]).filter((image) => image.naturalWidth === 0).length,
          ),
      )
      .toBe(0);

    // A section link keeps the administrator guide; User guide goes back.
    await help
      .getByRole('navigation', { name: 'Contents' })
      .getByRole('link', { name: 'Settings' })
      .click();
    await expect(help).toHaveURL(/#admin-settings$/);
    await expect(guide).toBeVisible();
    await help
      .getByRole('navigation', { name: 'Guides' })
      .getByRole('link', { name: 'User guide' })
      .click();
    await expect(help.locator('#guide-user')).toBeVisible();
    await expect(guide).toBeHidden();
  });
});
