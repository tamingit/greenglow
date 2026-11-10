import { expect, test } from '@playwright/test';
import { signIn } from './helpers.ts';

interface Manifest {
  name: string;
  start_url: string;
  display: string;
  icons: { src: string; sizes: string; purpose?: string }[];
}

test.describe('installing as an app', () => {
  test('the browser reads the app manifest without errors, and meets the install criteria', async ({
    page,
  }) => {
    await signIn(page, 'install@example.com');
    await expect(page.getByRole('button', { name: 'Connect to Fake IBM i' })).toBeVisible();
    const cdp = await page.context().newCDPSession(page);
    // The manifest as Chromium itself fetched and parsed it. (Its installability verdict isn't
    // usable in a headless test browser, so the criteria are checked below instead.)
    const read = (await cdp.send('Page.getAppManifest')) as {
      url: string;
      errors: { message: string }[];
      data?: string;
    };
    expect(read.url).toMatch(/\/manifest\.webmanifest$/);
    expect(read.errors).toEqual([]);
    const manifest = JSON.parse(read.data ?? '{}') as Manifest & { scope: string };
    // Chrome's install criteria: a name, 192 and 512 pixel icons, a start URL in scope, and
    // an app window (standalone). No service worker is needed, and none is used.
    expect(manifest.name).toBeTruthy();
    expect(manifest.icons.map((i) => i.sizes)).toEqual(
      expect.arrayContaining(['192x192', '512x512']),
    );
    expect(manifest.start_url.startsWith(manifest.scope)).toBe(true);
    expect(manifest.display).toBe('standalone');

    // A page that isn't the app has no manifest, so the check above is really looking.
    await page.goto('/auth/dev');
    expect(((await cdp.send('Page.getAppManifest')) as { url: string }).url).toBe('');
  });

  test('the manifest opens the app in its own window, with its icons', async ({
    page,
    request,
  }) => {
    await page.goto('/auth/dev');
    const href = await page.locator('link[rel=manifest]').count();
    expect(href).toBe(0); // the dev sign-in page is the server's, not the app's
    const manifest = (await (await request.get('/manifest.webmanifest')).json()) as Manifest;
    expect(manifest).toMatchObject({
      name: 'GreenGlow Connect',
      start_url: '/',
      display: 'standalone',
    });
    for (const icon of manifest.icons) {
      const response = await request.get(icon.src);
      expect(response.ok(), icon.src).toBe(true);
    }
    expect(manifest.icons.some((i) => i.purpose === 'maskable')).toBe(true);
    expect(manifest.icons.some((i) => i.sizes === '512x512')).toBe(true);
  });

  test('the admin page belongs to the same app', async ({ page }) => {
    await signIn(page, 'admin@example.com', '/admin.html');
    await expect(page.locator('link[rel=manifest]')).toHaveAttribute(
      'href',
      '/manifest.webmanifest',
    );
    // No service worker: nothing is cached for offline use.
    expect(
      await page.evaluate(() => navigator.serviceWorker.getRegistrations().then((r) => r.length)),
    ).toBe(0);
  });
});
