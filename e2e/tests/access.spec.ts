import { expect, test, type Page } from '@playwright/test';
import { signIn } from './helpers.ts';

/** The systems the page offers on the Disconnected card: a tile each, or one Connect button. */
async function offered(page: Page): Promise<string[]> {
  const card = page.locator('#terminal .offline');
  await expect(
    card.getByRole('button', { name: /Connect to|Not encrypted/ }).first(),
  ).toBeVisible();
  const tiles = card.getByRole('group', { name: 'Systems' }).locator('.offline-choice-label');
  if ((await tiles.count()) > 0) return tiles.allTextContents();
  const only = await card.getByRole('button', { name: /^Connect to / }).textContent();
  return [(only ?? '').replace(/^Connect to /, '')];
}

test.describe('connection access', () => {
  test('a user sees only the connections granted to their groups', async ({ page }) => {
    await signIn(page, 'someone@example.com');
    expect(await offered(page)).toEqual(['Fake IBM i']);
  });

  test('a group member sees the connection granted to that group', async ({ page }) => {
    await signIn(page, 'dev.user@example.com');
    expect(await offered(page)).toEqual(['Fake IBM i', 'Developers only']);
  });

  test('an administrator sees every connection', async ({ page }) => {
    await signIn(page, 'admin@example.com');
    expect(await offered(page)).toEqual(expect.arrayContaining(['Fake IBM i', 'Developers only']));
  });

  test('a connection not granted can’t be opened by name either', async ({ page }) => {
    await signIn(page, 'someone-else@example.com');
    const response = await page.request.post('/api/sessions', {
      data: { target: 'restricted' },
      headers: { origin: new URL(page.url()).origin },
    });
    expect(response.status()).toBe(404);
  });
});
