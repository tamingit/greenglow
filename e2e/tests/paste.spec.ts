import { expect, test, type Page } from '@playwright/test';
import { cell, connect, expectScreen, screenText, signIn, signOnFake, type } from './helpers.ts';

test.use({ permissions: ['clipboard-read', 'clipboard-write'] });

async function setClipboard(page: Page, text: string): Promise<void> {
  await page.evaluate((t) => navigator.clipboard.writeText(t), text);
}

async function clickCell(page: Page, row: number, column: number): Promise<void> {
  const at = await cell(page, row, column);
  await page.mouse.click(at.x, at.y);
}

async function rightClick(page: Page, row: number, column: number): Promise<void> {
  const at = await cell(page, row, column);
  await page.mouse.click(at.x, at.y, { button: 'right' });
}

test.describe('paste', () => {
  test('Ctrl+V types the clipboard from the cursor, a tab moving to the next field', async ({
    page,
  }) => {
    await signIn(page, 'paste1@example.com');
    await connect(page, 'Fake IBM i');
    await setClipboard(page, 'pat\tpw1');
    await clickCell(page, 6, 53);
    await page.keyboard.press('Control+v');
    await expect(page.locator('#status-text')).toHaveText('Pasted 6 characters.');
    // The report can come a frame before the screen is painted: wait for the text.
    await expect.poll(async () => (await screenText(page)).split('\n')[5]).toContain('PAT'); // monocase
    expect(await screenText(page)).not.toContain('pw1');
    // Pasting never presses Enter; the user does.
    await page.keyboard.press('Enter');
    await expectScreen(page, /IBM i Main Menu/);
  });

  test('the right-click menu selects all and copies the screen', async ({ page }) => {
    await signIn(page, 'paste2@example.com');
    await connect(page, 'Fake IBM i');
    await rightClick(page, 3, 10);
    const menu = page.getByRole('menu');
    await expect(menu.getByRole('menuitem', { name: /Copy/ })).toBeDisabled();
    await menu.getByRole('menuitem', { name: 'Select all' }).click();
    await expect(page.locator('#terminal .selection')).toBeVisible();
    await rightClick(page, 3, 10);
    await menu.getByRole('menuitem', { name: /Copy/ }).click();
    const copied = await page.evaluate(() => navigator.clipboard.readText());
    expect(copied).toContain('Sign On');
    expect(copied.split('\n').length).toBeGreaterThanOrEqual(24);
  });

  test('Paste from the menu stops where the screen refuses, and says so', async ({ page }) => {
    await signIn(page, 'paste3@example.com');
    await connect(page, 'Fake IBM i');
    await setClipboard(page, 'abc');
    await clickCell(page, 1, 1); // the title: protected
    await rightClick(page, 1, 1);
    await page.getByRole('menu').getByRole('menuitem', { name: /Paste/ }).click();
    await expect(page.locator('#status-text')).toContainText(
      'Pasted 0 of 3 characters: the screen refused',
    );
  });

  test('Esc closes the menu and the keys go back to the screen', async ({ page }) => {
    await signIn(page, 'paste4@example.com');
    await connect(page, 'Fake IBM i');
    await rightClick(page, 3, 10);
    await expect(page.getByRole('menu')).toBeVisible();
    await page.keyboard.press('Escape');
    await expect(page.getByRole('menu')).toBeHidden();
    await expect(page.locator('#terminal')).toBeFocused();
  });

  test('Copy entire table pages through the list and copies every row', async ({ page }) => {
    await signIn(page, 'paste.table@example.com');
    await connect(page, 'Fake IBM i');
    await signOnFake(page);
    await type(page, '4');
    await page.keyboard.press('Enter');
    await expectScreen(page, /Work with Placeholder Items/);

    // This page only, from the right-click menu.
    await rightClick(page, 8, 20);
    const menu = page.getByRole('menu');
    await menu.getByRole('menuitem', { name: /^Copy table/ }).click();
    await expect(page.locator('#status-text')).toHaveText('Copied the table: 5 rows.');
    let copied = await page.evaluate(() => navigator.clipboard.readText());
    expect(copied.split('\r\n')[0]).toBe('Item\tDescription\tQuantity');
    expect(copied.split('\r\n')[1]).toBe('ITEM001\tPlaceholder item 1\t3.00');

    // Every page with Alt+C: Page Down until Bottom, and the screen stays on the last page.
    await page.locator('#terminal').focus();
    await page.keyboard.press('Alt+C');
    await expect(page.locator('#status-text')).toHaveText(
      'Copied the table: 15 rows from 3 pages.',
    );
    copied = await page.evaluate(() => navigator.clipboard.readText());
    const lines = copied.trimEnd().split('\r\n');
    expect(lines).toHaveLength(16);
    expect(lines[4]).toBe('ITEM004\tPlaceholder item 4\t12.00-');
    expect(lines[15]).toBe('ITEM015\tPlaceholder item 15\t45.00');
    expect(await screenText(page)).toContain('Bottom');
  });
});
