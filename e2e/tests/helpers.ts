// Shared steps for the browser tests. Users sign in through the dev login (the server runs
// with Auth__DevLogin in the Testing environment); Entra ID itself is not exercised here.

import { expect, type Page } from '@playwright/test';

/** The initial administrator, from Entra__InitialAdminUpn in the e2e server. */
export const ADMIN = 'admin@example.com';

export const FAKE_USER = 'TESTER';
/** The fake IBM i accepts any password; this one checks it never shows or leaks. */
export const FAKE_PASSWORD = 'NOTSHOWN7';

/** Signs in as `upn` and lands on `path`. */
export async function signIn(page: Page, upn = 'tester@example.com', path = '/'): Promise<void> {
  await page.goto(path);
  await page.waitForURL(/\/auth\/dev/);
  await page.fill('input[name=upn]', upn);
  await page.fill('input[name=name]', upn.split('@')[0]);
  await page.click('button[type=submit]');
  await page.waitForURL((url) => !url.pathname.startsWith('/auth/'));
}

/** The terminal's visible text. */
export async function screenText(page: Page): Promise<string> {
  // The grid only: the overlay's card (and its list of sessions) is not the screen.
  return (await page.locator('#terminal .grid').innerText()).replace(/\u00a0/g, ' ');
}

export async function expectScreen(page: Page, text: string | RegExp): Promise<void> {
  await expect.poll(() => screenText(page), { timeout: 15_000 }).toMatch(text);
}

/**
 * Connects the active tab to a system (by its label) from the Disconnected card: its tile when
 * the user has several, else its Connect button. Waits for the first screen.
 */
export async function connect(page: Page, label: string): Promise<void> {
  const card = page.locator('#terminal .offline');
  const name = label.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  await card
    .getByRole('button', { name: new RegExp(`^(Connect to )?${name}`) })
    .first()
    .click();
  await expect(card).toBeHidden();
  await expectScreen(page, /Sign On|User/);
}

/** Disconnects the active tab, from the menu. */
export async function disconnect(page: Page): Promise<void> {
  await page.click('.bar-menu summary');
  await page.click('#menu-disconnect');
}

/** Types on the terminal as a user would. */
export async function type(page: Page, text: string): Promise<void> {
  await page.locator('#terminal').focus();
  await page.keyboard.type(text);
}

/** The centre of a screen cell (1-based), in page coordinates. */
export async function cell(page: Page, row: number, column: number, rows = 24, columns = 80) {
  const box = await page.locator('#terminal .grid').boundingBox();
  if (!box) throw new Error('no terminal grid');
  return {
    x: box.x + ((column - 0.5) / columns) * box.width,
    y: box.y + ((row - 0.5) / rows) * box.height,
  };
}

/** Signs on to the fake IBM i from its sign-on screen. */
export async function signOnFake(page: Page): Promise<void> {
  await type(page, FAKE_USER);
  await page.keyboard.press('Tab');
  await page.keyboard.type(FAKE_PASSWORD);
  await page.keyboard.press('Enter');
  await expectScreen(page, /IBM i Main Menu/);
}

/** Ends the user's sessions earlier tests left running, which the page would join on loading. */
export async function endSessions(page: Page): Promise<void> {
  const origin = new URL(page.url()).origin;
  const response = await page.request.get('/api/sessions');
  const { sessions } = (await response.json()) as { sessions: { id: string }[] };
  for (const { id } of sessions)
    await page.request.delete(`/api/sessions/${encodeURIComponent(id)}`, { headers: { origin } });
}
