import { expect, test } from '@playwright/test';
import { ADMIN, expectScreen, signOnFake } from './helpers.ts';

// A server under a base path (tools/src/e2e.ts starts it with Server__PublicUrl
// http://localhost:5099/greenglow), as behind a proxy that serves the app at /greenglow/. Every
// page, the API and the WebSockets must stay under it: the proxy sends nothing else there.
const ORIGIN = 'http://localhost:5099';
const BASE = `${ORIGIN}/greenglow`;

test.use({ baseURL: ORIGIN });

test('the whole app works under a base path', async ({ page }) => {
  const outside: string[] = [];
  const watch = (url: string) => {
    const address = new URL(url);
    // The base path alone is asked for once, and sent on to /greenglow/.
    if (
      address.origin === ORIGIN &&
      address.pathname !== '/greenglow' &&
      !address.pathname.startsWith('/greenglow/')
    )
      outside.push(address.pathname);
  };
  page.on('request', (request) => watch(request.url()));
  page.on('websocket', (socket) => watch(socket.url().replace(/^ws/, 'http')));

  // The base path alone: on to its first page, and to sign in, all under it.
  await page.goto(BASE);
  await page.waitForURL(`${BASE}/auth/dev**`);
  await page.fill('input[name=upn]', ADMIN);
  await page.click('button[type=submit]');
  await page.waitForURL(`${BASE}/`);

  // A session: the REST calls and its WebSocket.
  await page
    .locator('#terminal .offline')
    .getByRole('button', { name: /^(Connect to )?Fake IBM i/ })
    .first()
    .click();
  await expectScreen(page, /Sign On|User/);
  await signOnFake(page);

  // Administration, from the menu, and back.
  await page.click('.bar-menu summary');
  await page.click('#menu-admin');
  await page.waitForURL(`${BASE}/admin.html`);
  await page.getByRole('tab', { name: 'Branding' }).click();
  await expect(page.locator('#panel-branding').getByLabel('Address')).toHaveValue(BASE);
  await page.getByRole('link', { name: 'Back to the terminal' }).click();
  await page.waitForURL(`${BASE}/`);
  // The page rejoins the session it left.
  await expectScreen(page, /IBM i Main Menu/);

  // The help page and the administrator guide, with its pictures.
  await page.goto(`${BASE}/help.html#admin`);
  await expect(page.getByRole('heading', { name: /administrator guide/ })).toBeVisible();
  const picture = page.locator('img[src^="api/admin/guide/images/"]').first();
  await expect(picture).toBeVisible();
  expect(await picture.evaluate((img: HTMLImageElement) => img.naturalWidth)).toBeGreaterThan(0);

  // Signing out lands under the base path too.
  await page.goto(`${BASE}/`);
  await page.click('.bar-menu summary');
  await page.click('#menu-signout');
  await page.waitForURL(`${BASE}/auth/dev**`);

  expect(outside).toEqual([]);
});

test('the installed app starts under the base path', async ({ page }) => {
  const manifest = (await (await page.request.get(`${BASE}/manifest.webmanifest`)).json()) as {
    start_url: string;
    scope: string;
    icons: { src: string }[];
  };
  const at = (relative: string) => new URL(relative, `${BASE}/manifest.webmanifest`).href;
  expect(at(manifest.start_url)).toBe(`${BASE}/`);
  expect(at(manifest.scope)).toBe(`${BASE}/`);
  for (const icon of manifest.icons)
    expect((await page.request.get(at(icon.src))).headers()['content-type']).toBe('image/png');
});
