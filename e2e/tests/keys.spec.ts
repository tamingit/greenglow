import { expect, test } from '@playwright/test';
import {
  FAKE_PASSWORD,
  FAKE_USER,
  cell,
  connect,
  endSessions,
  expectScreen,
  screenText,
  signIn,
  signOnFake,
  type,
} from './helpers.ts';

test.describe('keys to tap', () => {
  test('the keys panel beside the screen sends the key pressed, in two columns', async ({
    page,
  }) => {
    await signIn(page, 'keys.panel@example.com');
    // A retry starts clean: the page would join the session an earlier try left running.
    await endSessions(page);
    await page.goto('/');
    await connect(page, 'Fake IBM i');
    await signOnFake(page);
    await page.locator('#terminal').focus();
    await page.keyboard.press('Alt+K');
    const panel = page.getByRole('complementary', { name: 'Keys' });
    const keys = panel.locator('.keys-panel-key');
    await expect(keys).toHaveText([
      ...Array.from({ length: 24 }, (_, i) => `F${i + 1}`),
      'Field Exit',
      'Erase Input',
      'Page Up',
      'Page Down',
      'Enter',
      // Other keys, folded until opened.
      'Previous field',
      'Next field',
      'Field +',
      'Field −',
      'New Line',
      'Erase EOF',
      'Reset',
      'Dup',
      'Insert',
      'Delete',
      'Home',
      'End',
      'Help',
      'Print',
      'Clear',
      'Attention',
      'Sys Req',
    ]);
    await expect(panel.getByRole('button', { name: 'Help', exact: true })).toBeHidden();
    await panel.getByText('Other keys').click();
    await expect(panel.getByRole('button', { name: 'Help', exact: true })).toBeVisible();
    // Beside the main keys, the panel wider, with nothing to scroll.
    const help = (await panel.getByRole('button', { name: 'Help', exact: true }).boundingBox())!;
    const right = (await panel.getByRole('button', { name: 'F2', exact: true }).boundingBox())!;
    expect(help.x).toBeGreaterThan(right.x + right.width);
    expect(await panel.evaluate((p) => p.scrollHeight <= p.clientHeight)).toBe(true);
    // F1 | F2 side by side, F3 under F1; Enter across both columns.
    const box = async (name: string) =>
      (await panel.getByRole('button', { name, exact: true }).boundingBox())!;
    const [f1, f2, f3, enter] = await Promise.all(['F1', 'F2', 'F3', 'Enter'].map(box));
    expect(Math.round(f2.y)).toBe(Math.round(f1.y));
    expect(f2.x).toBeGreaterThan(f1.x);
    expect(Math.round(f3.x)).toBe(Math.round(f1.x));
    expect(enter.width).toBeGreaterThan(f1.width * 1.5);

    // A press sends the key and leaves the keys on the screen: Enter takes option 4.
    await type(page, '4');
    await panel.getByRole('button', { name: 'Enter', exact: true }).click();
    await expectScreen(page, /Work with Placeholder Items/);
    await expect(page.locator('#terminal')).toBeFocused();
    // Kept for next time; Alt+K closes it.
    await page.reload();
    await expect(panel).toBeVisible();
    await page.locator('#terminal').focus();
    await page.keyboard.press('Alt+K');
    await expect(panel).toBeHidden();
  });

  test('the side tabs pull out one panel at a time from the window’s edge', async ({ page }) => {
    await signIn(page, 'keys.side@example.com');
    await connect(page, 'Fake IBM i');
    const tab = (name: string) => page.locator('.side-tab', { hasText: name });
    const keys = page.getByRole('complementary', { name: 'Keys' });
    const sharing = page.getByRole('complementary', { name: 'Sharing' });
    const macros = page.getByRole('complementary', { name: 'Macros' });

    await tab('Keys').click();
    await expect(keys).toBeVisible();
    await expect(tab('Keys')).toHaveAttribute('aria-expanded', 'true');
    // Against the window's right edge, with the screen clear of it.
    const width = page.viewportSize()!.width;
    const panel = (await keys.boundingBox())!;
    const terminal = (await page.locator('#terminal').boundingBox())!;
    expect(Math.round(panel.x + panel.width)).toBeLessThanOrEqual(width);
    expect(terminal.x + terminal.width).toBeLessThanOrEqual(panel.x);

    // Another tab swaps it; the open one's tab pushes it back.
    await tab('Macros').click();
    await expect(macros.getByRole('list', { name: 'Your macros' })).toContainText('None yet.');
    await expect(keys).toBeHidden();
    await page.keyboard.press('Alt+S');
    await expect(sharing).toBeVisible();
    await expect(macros).toBeHidden();
    await tab('Share').click();
    await expect(sharing).toBeHidden();
    await expect(tab('Share')).toHaveAttribute('aria-expanded', 'false');
  });

  test('on a touch screen, a swipe pages the list and its rows slide in', async ({ browser }) => {
    const touch = await browser.newContext({ hasTouch: true });
    const page = await touch.newPage();
    await signIn(page, 'keys.swipe@example.com');
    await connect(page, 'Fake IBM i');
    await signOnFake(page);
    await type(page, '4');
    await page.keyboard.press('Enter');
    await expectScreen(page, /ITEM001/);

    const swipe = async (fromY: number, toY: number) => {
      const terminal = page.locator('#terminal');
      const box = (await terminal.boundingBox())!;
      const x = box.x + box.width / 2;
      const at = (y: number) => ({
        pointerType: 'touch',
        pointerId: 7,
        clientX: x,
        clientY: box.y + y,
      });
      await terminal.dispatchEvent('pointerdown', at(fromY));
      for (let i = 1; i <= 5; i++)
        await terminal.dispatchEvent('pointermove', at(fromY + ((toY - fromY) * i) / 5));
      await terminal.dispatchEvent('pointerup', at(toY));
    };
    // Up the screen, far enough for one page: Page Down, and the new rows slide up into place.
    await swipe(400, 250);
    await expectScreen(page, /ITEM006/);
    await expect(page.locator('#terminal .row.slide-up').first()).toBeAttached();
    // Down the screen: Page Up.
    await swipe(250, 400);
    await expectScreen(page, /ITEM001/);
    await expect(page.locator('#terminal .row.slide-down').first()).toBeAttached();
    await touch.close();
  });

  test('on a touch screen, tapping a field brings up the keyboard and typing goes into it', async ({
    browser,
  }) => {
    const touch = await browser.newContext({ hasTouch: true });
    const page = await touch.newPage();
    await signIn(page, 'keys.soft@example.com');
    await connect(page, 'Fake IBM i');
    const softFocused = () =>
      page.evaluate(() => document.activeElement?.classList.contains('soft-input') ?? false);

    // The User field starts at row 6, column 53; text from an on-screen keyboard arrives as
    // input, with no key events.
    const user = await cell(page, 6, 53);
    await page.touchscreen.tap(user.x, user.y);
    await expect.poll(softFocused).toBe(true);
    await page.keyboard.insertText(FAKE_USER);
    await expect.poll(() => screenText(page)).toContain(FAKE_USER);

    // A tap on the next field keeps the keyboard up; a line break is Enter.
    const password = await cell(page, 7, 53);
    await page.touchscreen.tap(password.x, password.y);
    await expect.poll(softFocused).toBe(true);
    await page.keyboard.insertText(FAKE_PASSWORD);
    await page.keyboard.insertText('\n');
    await expectScreen(page, /IBM i Main Menu/);
    expect(await screenText(page)).not.toContain(FAKE_PASSWORD);

    // A tap off the fields puts the keyboard away.
    const title = await cell(page, 4, 3);
    await page.touchscreen.tap(title.x, title.y);
    await expect.poll(softFocused).toBe(false);
    await touch.close();
  });
});
