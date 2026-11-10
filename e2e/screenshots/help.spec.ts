// The help page's screenshots (`npm run screenshots`). Everything shown comes from the fake IBM i
// and example.com people: placeholder text only. Each step waits for what it shows, so the
// pictures are the same from run to run.

import * as path from 'node:path';
import { expect, test, type Locator, type Page } from '@playwright/test';
import {
  ADMIN,
  FAKE_PASSWORD,
  FAKE_USER,
  cell,
  endSessions,
  expectScreen,
  signIn,
  signOnFake,
  type,
} from '../tests/helpers.ts';

const root = path.resolve(import.meta.dirname, '../..');
const userDir = path.join(root, 'packages/web/public/help');
const adminDir = path.join(root, 'server/src/GreenGlow.Server/Help');

const USER = 'pat.lee@example.com';
const FRIEND = 'sam.ortiz@example.com';

/** A picture of the whole window. */
async function shot(page: Page, name: string): Promise<void> {
  await page.mouse.move(0, 0);
  await page.screenshot({ path: path.join(userDir, name), animations: 'disabled' });
}

/** A picture of one element, with a margin of the page around it; down to `end`'s bottom when given. */
async function shotOf(page: Page, element: Locator, name: string, end?: Locator): Promise<void> {
  const box = (await element.boundingBox())!;
  if (end) {
    const last = (await end.boundingBox())!;
    box.height = last.y + last.height - box.y;
  }
  const viewport = page.viewportSize()!;
  const margin = 12;
  const x = Math.max(0, box.x - margin);
  const y = Math.max(0, box.y - margin);
  await page.screenshot({
    path: path.join(userDir, name),
    animations: 'disabled',
    clip: {
      x,
      y,
      width: Math.min(viewport.width - x, box.width + 2 * margin),
      height: Math.min(viewport.height - y, box.height + 2 * margin),
    },
  });
}

async function api(page: Page, method: string, url: string, data?: unknown) {
  const origin = new URL(page.url()).origin;
  const response = await page.request.fetch(url, { method, data, headers: { origin } });
  expect(response.ok(), `${method} ${url}: ${response.status()}`).toBeTruthy();
  return response.status() === 204 ? null : ((await response.json()) as Record<string, unknown>);
}

/** Two systems with colours, both for everyone, so the card shows tiles. */
async function systems(page: Page): Promise<void> {
  const { connections } = (await api(page, 'GET', '/api/admin/connections')) as {
    connections: { id: number; name: string }[];
  };
  const byName = new Map(connections.map((c) => [c.name, c.id]));
  await api(page, 'PUT', `/api/admin/connections/${byName.get('fake')}`, {
    label: 'Development',
    color: '#2f7d4f',
  });
  if (byName.has('restricted'))
    await api(page, 'PUT', `/api/admin/connections/${byName.get('restricted')}`, {
      enabled: false,
    });
  if (!byName.has('test')) {
    const created = (await api(page, 'POST', '/api/admin/connections', {
      name: 'test',
      label: 'Test',
      url: 'tcp://127.0.0.1:2323',
      maxSessionsPerUser: 3,
      isTest: true,
      enabled: true,
      color: '#9a5b13',
    })) as { id: number };
    const { groups } = (await api(page, 'GET', '/api/admin/directory/groups?q=Everyone')) as {
      groups: { id: string; name: string }[];
    };
    await api(page, 'POST', `/api/admin/connections/${created.id}/grants`, {
      type: 'group',
      id: groups[0].id,
      name: groups[0].name,
    });
  }
}

async function menu(page: Page): Promise<Locator> {
  await page.click('.bar-menu summary');
  return page.locator('.bar-menu-panel');
}

async function closeMenu(page: Page): Promise<void> {
  await page.keyboard.press('Escape');
  await expect(page.locator('.bar-menu-panel')).toBeHidden();
}

async function chooseSystem(page: Page, label: string): Promise<void> {
  const card = page.locator('#terminal .offline');
  await card
    .getByRole('button', { name: new RegExp(`^${label}`) })
    .first()
    .click();
  await expect(card).toBeHidden();
  await expectScreen(page, /Sign On/);
}

test.describe.serial('help screenshots', () => {
  test('the user guide', async ({ page, browser }) => {
    await signIn(page, ADMIN, '/admin.html');
    await systems(page);
    await endSessions(page);

    await page.context().clearCookies();
    await signIn(page, USER);
    await endSessions(page);
    await page.goto('/');
    const card = page.locator('#terminal .offline');
    await expect(card.getByRole('button', { name: /^Test/ })).toBeVisible();
    await shot(page, 'lobby.png');

    await chooseSystem(page, 'Development');
    await type(page, FAKE_USER);
    await shot(page, 'signon.png');
    await page.keyboard.press('Tab');
    await page.keyboard.type(FAKE_PASSWORD);
    await page.keyboard.press('Enter');
    await expectScreen(page, /IBM i Main Menu/);

    // A second session, on the other system.
    await page.keyboard.press('Alt+N');
    await chooseSystem(page, 'Test');
    await signOnFake(page);
    await page.keyboard.press('Alt+1');
    await expectScreen(page, /IBM i Main Menu/);

    // The menu is taller than the window; grow the window to fit it for this picture only.
    const viewport = page.viewportSize()!;
    const panel = await menu(page);
    const bottom = await panel.evaluate((el) => el.getBoundingClientRect().bottom);
    await page.setViewportSize({
      ...viewport,
      height: Math.max(viewport.height, Math.ceil(bottom) + 24),
    });
    await shotOf(page, panel, 'menu.png');
    await closeMenu(page);
    await page.setViewportSize(viewport);

    // A block selected, and the right-click menu.
    const from = await cell(page, 6, 7);
    const to = await cell(page, 9, 38);
    await page.mouse.move(from.x, from.y);
    await page.mouse.down();
    await page.mouse.move(to.x, to.y, { steps: 5 });
    await page.mouse.up();
    await page.mouse.click(to.x, to.y, { button: 'right' });
    await expect(page.locator('.context-menu')).toBeVisible();
    await page.screenshot({ path: path.join(userDir, 'context.png'), animations: 'disabled' });
    await page.keyboard.press('Escape');

    // The list, in the GreenGlow look.
    await (
      await menu(page)
    )
      .getByRole('radiogroup', { name: 'Look' })
      .getByText('GreenGlow')
      .click();
    await closeMenu(page);
    await type(page, '4');
    await page.keyboard.press('Enter');
    await expectScreen(page, /Work with Placeholder Items/);
    await shot(page, 'greenglow.png');

    // The Keys panel with Other keys open.
    await page.keyboard.press('Alt+K');
    const keys = page.getByRole('complementary', { name: 'Keys' });
    await keys.getByText('Other keys').click();
    await expect(keys.getByRole('button', { name: 'Sys Req' })).toBeVisible();
    await shotOf(page, keys, 'keys.png', keys.getByRole('button', { name: 'Enter', exact: true }));
    await page.keyboard.press('Alt+K');

    // Sharing: the dialog, then the panel with the friend watching.
    await (await menu(page)).getByText('Share this session…').click();
    const dialog = page.locator('.share-dialog');
    await dialog.getByLabel('Add a person').fill(FRIEND);
    await page.getByRole('option', { name: new RegExp(FRIEND.replace('.', '\\.')) }).click();
    await dialog.getByRole('combobox').last().selectOption({ label: 'Full Access' });
    await dialog.getByRole('button', { name: 'Create link' }).click();
    const link = await dialog.locator('input[readonly]').first().inputValue();
    await shotOf(page, dialog, 'share.png');
    await page.keyboard.press('Escape');

    const friendContext = await browser.newContext({ viewport: { width: 1280, height: 820 } });
    const friend = await friendContext.newPage();
    await signIn(friend, FRIEND);
    await friend.goto(link);
    await expectScreen(friend, /Work with Placeholder Items/);
    await page.keyboard.press('Alt+S');
    const sharing = page.getByRole('complementary', { name: 'Sharing' });
    await expect(sharing.getByText('sam.ortiz')).toBeVisible();
    await shotOf(page, sharing, 'sharing-panel.png', sharing.getByText('None now.'));
    await page.keyboard.press('Alt+S');
    await friendContext.close();

    // Macros: one made here, one imported from ACS.
    await page.keyboard.press('Alt+M');
    const macros = page.getByRole('complementary', { name: 'Macros' });
    await macros.getByRole('button', { name: 'New macro' }).click();
    await macros.getByLabel('New macro name').fill('Month-end report');
    await macros.getByLabel('New macro name').press('Enter');
    const mac =
      '<HAScript name="Stock check" description="Weekly" timeout="60000" pausetime="300">\n' +
      '  <screen name="Screen1" entryscreen="true" exitscreen="true" transient="false">\n' +
      '    <description><oia status="NOTINHIBITED" optional="false" invertmatch="false" /></description>\n' +
      '    <actions><input value="\'[pf3]\'" row="0" col="0" movecursor="true" xlatehostkeys="true" encrypted="false" /></actions>\n' +
      '    <nextscreens timeout="0"></nextscreens>\n' +
      '  </screen>\n' +
      '</HAScript>\n';
    const choosing = page.waitForEvent('filechooser');
    await macros.getByRole('button', { name: 'Import a macro (.mac)' }).click();
    await (
      await choosing
    ).setFiles({ name: 'stock.mac', mimeType: 'text/xml', buffer: Buffer.from(mac) });
    await expect(macros.getByRole('list', { name: 'Your macros' })).toContainText('Stock check');
    await shotOf(page, macros, 'macros.png', macros.getByRole('list', { name: 'Your macros' }));
    await page.keyboard.press('Alt+M');

    // Printer output, signed on to the fake's print server.
    await page.keyboard.press('Alt+P');
    const printer = page.getByRole('complementary', { name: 'Printer output' });
    await printer.getByLabel('IBM i user').fill('PRTUSER');
    await printer.getByLabel('Password').fill('Secret12');
    await printer.getByLabel('Password').press('Enter');
    const spooled = printer.getByRole('list', { name: 'Your spooled files' });
    await expect(spooled.getByRole('button')).toHaveCount(3);
    await shotOf(page, printer, 'printer.png', spooled);
    await page.keyboard.press('Alt+P');

    // Customize colours.
    await (await menu(page)).getByText('Customize colours…').click();
    const theme = page.locator('.theme-dialog');
    await expect(theme).toBeVisible();
    await shotOf(page, theme, 'colours.png');
    await theme.getByRole('button', { name: 'Done' }).click();

    // Another window: its card lists the sessions open in this one.
    const otherContext = await browser.newContext({ viewport: { width: 1280, height: 820 } });
    const other = await otherContext.newPage();
    await signIn(other, USER);
    const otherCard = other.locator('#terminal .offline');
    await expect(otherCard.getByRole('table')).toBeVisible();
    await expect(otherCard.getByRole('cell', { name: 'Test', exact: true })).toBeVisible();
    await shot(other, 'sessions.png');
    await otherContext.close();

    // Disconnecting while signed on asks first.
    await (await menu(page)).getByText('Disconnect').click();
    await expect(card.getByRole('button', { name: 'Stay connected' })).toBeVisible();
    await shot(page, 'disconnect.png');
    await card.getByRole('button', { name: 'Stay connected' }).click();

    await endSessions(page);
  });

  test('the administrator guide', async ({ page }) => {
    await signIn(page, ADMIN, '/admin.html#connections');
    await expect(page.getByText('Development').first()).toBeVisible();
    await page.waitForLoadState('networkidle');
    await page.screenshot({
      path: path.join(adminDir, 'admin-connections.png'),
      animations: 'disabled',
    });
    for (const [tab, file] of [
      ['Connection log', 'admin-log.png'],
      ['Administrators', 'admin-admins.png'],
      ['Settings', 'admin-settings.png'],
    ] as const) {
      await page.getByRole('tab', { name: tab }).click();
      await page.waitForLoadState('networkidle');
      await page.screenshot({ path: path.join(adminDir, file), animations: 'disabled' });
    }
  });
});
