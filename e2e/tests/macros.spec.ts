import { expect, test, type Page } from '@playwright/test';
import { signIn } from './helpers.ts';

/** Pulls the Macros panel out from its tab on the window's edge. */
async function macrosPanel(page: Page) {
  await page.locator('.side-tab', { hasText: 'Macros' }).click();
  return page.getByRole('complementary', { name: 'Macros' });
}

test.describe('macros', () => {
  test('a user makes a macro, shares it with someone, and they see it', async ({
    page,
    browser,
  }) => {
    await signIn(page, 'macro.owner@example.com');
    const panel = await macrosPanel(page);
    await panel.getByRole('button', { name: 'New macro' }).click();
    await panel.getByLabel('New macro name').fill('Month end');
    await panel.getByLabel('New macro name').press('Enter');
    const mine = panel.getByRole('list', { name: 'Your macros' });
    await expect(mine).toContainText('Month end');

    await mine.getByRole('button', { name: 'Share Month end' }).click();
    const dialog = page.getByRole('dialog', { name: 'Share the macro Month end' });
    await dialog.getByLabel('Add a person').fill('macro.friend@example.com');
    await page.getByRole('option', { name: /macro\.friend@example\.com/ }).click();
    await expect(dialog.getByRole('list', { name: 'Shared with' })).toContainText(
      'macro.friend@example.com',
    );
    await dialog.getByRole('button', { name: 'Close' }).click();
    await expect(mine).toContainText('shared with 1');

    const context = await browser.newContext();
    const friend = await context.newPage();
    await signIn(friend, 'macro.friend@example.com');
    const theirs = (await macrosPanel(friend)).getByRole('list', {
      name: 'Macros shared with you',
    });
    await expect(theirs).toContainText('Month end');
    await expect(theirs).toContainText('macro.owner@example.com');
    // Shared is play-only: play, keys and export, but no share, rename or delete.
    await expect(theirs.getByRole('button')).toHaveText(['▶', '⌨', '⇧']);
    await expect(theirs.getByRole('button', { name: 'Export Month end' })).toBeVisible();
    await context.close();
  });

  test('a .mac file from ACS imports, and exports again as the same macro', async ({ page }) => {
    await signIn(page, 'macro.import@example.com');
    const panel = await macrosPanel(page);
    const mac =
      '<HAScript name="From ACS" description="Placeholder" timeout="60000" pausetime="300">\n' +
      '    <screen name="Screen1" entryscreen="true" exitscreen="true" transient="false">\n' +
      '        <description><oia status="NOTINHIBITED" optional="false" invertmatch="false" /></description>\n' +
      '        <actions><input value="\'[pf3]\'" row="0" col="0" movecursor="true" xlatehostkeys="true" encrypted="false" /></actions>\n' +
      '        <nextscreens timeout="0"></nextscreens>\n' +
      '    </screen>\n' +
      '</HAScript>\n';
    const choosing = page.waitForEvent('filechooser');
    await panel.getByRole('button', { name: 'Import a macro (.mac)' }).click();
    await (
      await choosing
    ).setFiles({ name: 'from-acs.mac', mimeType: 'text/xml', buffer: Buffer.from(mac) });
    const mine = panel.getByRole('list', { name: 'Your macros' });
    await expect(mine).toContainText('From ACS');

    const downloading = page.waitForEvent('download');
    await mine.getByRole('button', { name: 'Export From ACS' }).click();
    const download = await downloading;
    expect(download.suggestedFilename()).toBe('From ACS.mac');
    const saved = await download.path();
    const { readFile } = await import('node:fs/promises');
    expect(await readFile(saved, 'utf8')).toBe(mac);

    // One holding a typed password is refused, with the reason.
    const withPassword = mac.replace(
      'value="\'[pf3]\'" row="0" col="0" movecursor="true" xlatehostkeys="true" encrypted="false"',
      'value="c2VjcmV0" row="7" col="53" movecursor="true" xlatehostkeys="true" encrypted="true"',
    );
    const again = page.waitForEvent('filechooser');
    await panel.getByRole('button', { name: 'Import a macro (.mac)' }).click();
    await (
      await again
    ).setFiles({ name: 'password.mac', mimeType: 'text/xml', buffer: Buffer.from(withPassword) });
    await expect(panel.getByRole('status')).toContainText('holds a typed password');
  });

  test('a macro gets its own keys, which play it', async ({ page }) => {
    await signIn(page, 'macro.keys@example.com');
    const panel = await macrosPanel(page);
    await panel.getByRole('button', { name: 'New macro' }).click();
    await panel.getByLabel('New macro name').fill('Quick');
    await panel.getByLabel('New macro name').press('Enter');
    const mine = panel.getByRole('list', { name: 'Your macros' });
    await mine.getByRole('button', { name: 'Keys for Quick' }).click();
    await page.keyboard.press('Alt+Shift+Q');
    await expect(mine).toContainText('Alt+Shift+Q');

    // Pressed with no session open, it says why it can't play.
    await page.locator('#terminal').focus();
    await page.keyboard.press('Alt+Shift+Q');
    await expect(panel.getByRole('status')).toHaveText('Open a session to play it on first.');
  });
});
