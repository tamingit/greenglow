import { expect, test, type Page } from '@playwright/test';
import { ADMIN, signIn } from './helpers.ts';

// An icon to pick: a plain SVG, drawn into PNGs by the admin page.
const ICON = Buffer.from(
  '<svg xmlns="http://www.w3.org/2000/svg" width="64" height="64"><circle cx="32" cy="32" r="30" fill="#ffcc00"/></svg>',
);

/** Back to GreenGlow's own brand and the configured address, for the tests after this one. */
async function reset(page: Page): Promise<void> {
  await page.evaluate(async () => {
    const send = (method: string, path: string, body?: unknown) =>
      fetch(path, {
        method,
        headers: body === undefined ? {} : { 'content-type': 'application/json' },
        body: body === undefined ? undefined : JSON.stringify(body),
      });
    await send('PUT', '/api/admin/branding', { name: '', shortName: '', color: '' });
    await send('DELETE', '/api/admin/branding/icon');
    await send('PUT', '/api/admin/address', { url: null });
  });
}

test.describe('branding', () => {
  test.afterEach(async ({ page }) => {
    await reset(page);
  });

  test('an administrator renames, recolours and gives the app a new icon', async ({ page }) => {
    await signIn(page, ADMIN);
    await page.goto('/admin.html#branding');
    const panel = page.locator('#panel-branding');

    await panel.getByLabel('Name', { exact: true }).fill('Acme Terminal');
    await panel.getByLabel('Short name').fill('Acme');
    await panel
      .locator('section', { hasText: 'Short name' })
      .getByRole('button', { name: 'Save' })
      .click();
    await expect(page.locator('.bar-brand-name')).toHaveText('Acme Terminal');
    await expect(page).toHaveTitle('Acme Terminal administration');

    await panel.getByLabel('Brand colour as #rrggbb').fill('#7a1f5c');
    await panel
      .locator('section', { hasText: 'Brand colour' })
      .getByRole('button', { name: 'Save' })
      .click();
    const bar = page.locator('.app-bar');
    await expect(bar).toHaveCSS('background-color', 'rgb(122, 31, 92)');

    await panel.getByLabel('Image').setInputFiles({
      name: 'icon.svg',
      mimeType: 'image/svg+xml',
      buffer: ICON,
    });
    await expect(panel.locator('img.admin-brand-icon')).toHaveAttribute(
      'src',
      /^branding\/icon-192\.png\?v=/,
    );

    // Every page is served with the brand, from the first paint.
    await page.goto('/');
    await expect(page).toHaveTitle('Acme Terminal');
    await expect(bar).toHaveCSS('background-color', 'rgb(122, 31, 92)');
    await expect(page.locator('img.bar-brand-mark')).toHaveAttribute('src', /^branding\//);
    await expect(page.locator('meta[name="theme-color"]')).toHaveAttribute('content', '#7a1f5c');
    const icon = await page.request.get('/branding/icon-512.png');
    expect(icon.headers()['content-type']).toBe('image/png');
    const manifest = (await (await page.request.get('/manifest.webmanifest')).json()) as {
      name: string;
      short_name: string;
    };
    expect([manifest.name, manifest.short_name]).toEqual(['Acme Terminal', 'Acme']);

    // And back.
    await page.goto('/admin.html#branding');
    await panel.getByRole('button', { name: 'Use GreenGlow’s name' }).click();
    await expect(page.locator('.bar-brand-name')).toHaveText('GreenGlow Connect');
    await panel.getByRole('button', { name: 'Use GreenGlow’s icon' }).click();
    await expect(panel.locator('img.admin-brand-icon')).toHaveAttribute(
      'src',
      'icons/icon-192.png',
    );
  });

  test('an administrator changes the address people open it at', async ({ page, baseURL }) => {
    await signIn(page, ADMIN);
    await page.goto('/admin.html#branding');
    const card = page.locator('#panel-branding section', { hasText: 'Address' });
    await expect(card.getByLabel('Address')).toHaveValue(baseURL!.replace(/\/$/, ''));
    await expect(card).toContainText('from configuration');

    await card.getByLabel('Address').fill('https://terminal.example.com');
    page.once('dialog', (dialog) => void dialog.accept());
    await card.getByRole('button', { name: 'Save' }).click();
    await expect(page.locator('#admin-message')).toHaveText(
      'The address is https://terminal.example.com.',
    );
  });
});
