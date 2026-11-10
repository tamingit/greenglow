import { expect, test } from '@playwright/test';
import {
  FAKE_PASSWORD,
  cell,
  connect,
  endSessions,
  expectScreen,
  screenText,
  signIn,
  signOnFake,
  type,
} from './helpers.ts';

test.describe('terminal', () => {
  test('a signed-out visitor is sent to sign in', async ({ page }) => {
    await page.goto('/');
    await expect(page).toHaveURL(/\/auth\/dev\?returnUrl=%2F/);
  });

  test('signs on to the fake IBM i and reaches the menu', async ({ page }) => {
    await signIn(page, 'terminal1@example.com');
    await expect(page.locator('#user-upn')).toHaveText('terminal1@example.com');
    await connect(page, 'Fake IBM i');
    await expectScreen(page, /Sign On/);
    await signOnFake(page);
    expect(await screenText(page)).not.toContain(FAKE_PASSWORD);
  });

  test('the rule line follows the cursor and is kept for next time', async ({ page }) => {
    await signIn(page, 'terminal.rule@example.com');
    await endSessions(page);
    await page.goto('/');
    await connect(page, 'Fake IBM i');
    const row = page.locator('#terminal .rule-row');
    const column = page.locator('#terminal .rule-column');
    await expect(row).toBeHidden();
    await expect(column).toBeHidden();

    await page.click('.bar-menu summary');
    await page.getByRole('radiogroup', { name: 'Rule line' }).getByText('Crosshair').click();
    await expect(row).toBeVisible();
    await expect(column).toBeVisible();
    // Through the cursor's cell: along the bottom of its row, down the left of its column.
    const lines = async () => {
      const cursor = (await page.locator('#terminal .cursor').boundingBox())!;
      const r = (await row.boundingBox())!;
      const c = (await column.boundingBox())!;
      return [
        r.y >= cursor.y && r.y + r.height <= cursor.y + cursor.height,
        Math.round(c.x - cursor.x),
      ];
    };
    await expect.poll(lines).toEqual([true, 0]);
    await page.locator('#terminal').focus();
    await page.keyboard.press('Tab');
    await expect.poll(lines).toEqual([true, 0]);

    // The page joins its session again after a reload, and the rule line is still on.
    await page.reload();
    await expectScreen(page, /Sign On/);
    await expect(row).toBeVisible();
    await page.click('.bar-menu summary');
    await page.getByRole('radiogroup', { name: 'Rule line' }).getByText('Row').click();
    await expect(row).toBeVisible();
    await expect(column).toBeHidden();
  });

  test('a user scales, stretches or fixes the size of the screen, and keeps the choice', async ({
    page,
  }) => {
    // A short, wide window: wider than 96rem, which the screen may use all of.
    await page.setViewportSize({ width: 2000, height: 700 });
    await signIn(page, 'terminal.size@example.com');
    await endSessions(page);
    await page.goto('/');
    await connect(page, 'Fake IBM i');
    await expectScreen(page, /Sign On/);
    const grid = page.locator('#terminal .grid');
    const stage = page.locator('#stage');
    const width = async () => Math.round((await grid.boundingBox())!.width);
    const fontSize = () =>
      page.locator('#terminal').evaluate((t) => t.style.getPropertyValue('--font-size'));
    const sizes = page.getByRole('radiogroup', { name: 'Screen size' });

    // Scale (the default) keeps the screen's shape: in a short, wide window it fills the
    // height and is narrower than the stage, and in a tall one it fills the width.
    const stageWidth = Math.round((await stage.boundingBox())!.width);
    expect(stageWidth).toBeGreaterThan(2000 - 100);
    const scaled = await width();
    expect(scaled).toBeLessThan(stageWidth - 100);
    const fillsHeight = async () => {
      const box = (await page.locator('#terminal').boundingBox())!;
      const stageBox = (await stage.boundingBox())!;
      return stageBox.y + stageBox.height - (box.y + box.height);
    };
    expect(await fillsHeight()).toBeLessThan(40);
    await page.setViewportSize({ width: 2000, height: 2400 });
    await expect.poll(width).toBeGreaterThan(2000 - 100);
    await page.setViewportSize({ width: 2000, height: 700 });
    await expect.poll(width).toEqual(scaled);
    await expect(page.locator('#font-size')).toBeHidden();

    // Stretch fills the width, and refits beside a side panel.
    await page.click('.bar-menu summary');
    await sizes.getByText('Stretch').click();
    await page.keyboard.press('Escape');
    await expect.poll(width).toBeGreaterThan(2000 - 100);
    const stretched = await width();
    await page.locator('#terminal').focus();
    await page.keyboard.press('Alt+K');
    await expect(page.getByRole('complementary', { name: 'Keys' })).toBeVisible();
    await expect.poll(width).toBeLessThan(stretched - 100);
    await page.keyboard.press('Alt+K');
    await expect.poll(width).toEqual(stretched);

    // Fixed takes the font size chosen, whatever the space, and the screen is only as wide
    // as its columns.
    await page.click('.bar-menu summary');
    await sizes.getByText('Fixed').click();
    await page.locator('#font-size').selectOption('24');
    await expect.poll(fontSize).toBe('24px');
    const fixed = await width();
    const terminalWidth = async () =>
      Math.round((await page.locator('#terminal').boundingBox())!.width);
    expect((await terminalWidth()) - fixed).toBeLessThan(40);
    await page.setViewportSize({ width: 2200, height: 800 });
    await expect.poll(width).toEqual(fixed);

    // Kept after a reload.
    await page.reload();
    await expectScreen(page, /Sign On/);
    await expect.poll(fontSize).toBe('24px');
    await page.click('.bar-menu summary');
    await expect(sizes.getByRole('radio', { name: 'Fixed' })).toBeChecked();
    await sizes.getByText('Scale').click();
    await expect(page.locator('#font-size')).toBeHidden();
  });

  test('a user customizes the colours, keeps them, and saves and loads them as a file', async ({
    page,
  }, info) => {
    await signIn(page, 'terminal.theme@example.com');
    await connect(page, 'Fake IBM i');
    const terminal = page.locator('#terminal');
    const background = () => terminal.evaluate((t) => getComputedStyle(t).backgroundColor);
    const classic = await background();

    await page.click('.bar-menu summary');
    await page.getByRole('button', { name: 'Customize colours…' }).click();
    const dialog = page.getByRole('dialog', { name: 'Customize Classic' });
    await dialog.getByLabel('Background').fill('#203040');
    await expect.poll(background).toBe('rgb(32, 48, 64)');
    await dialog.getByLabel('Theme name').fill('Harbour');

    // Saved to a file: the colours as #rrggbb, for the look they were made for.
    const downloading = page.waitForEvent('download');
    await dialog.getByRole('button', { name: 'Save to a file' }).click();
    const saved = info.outputPath('harbour.json');
    await (await downloading).saveAs(saved);
    await dialog.getByRole('button', { name: 'Done' }).click();

    // Kept in this browser.
    await page.reload();
    await expectScreen(page, /Sign On/);
    await expect.poll(background).toBe('rgb(32, 48, 64)');

    // Back to Classic's own colours, then the file brings them back.
    await page.click('.bar-menu summary');
    await page.getByRole('button', { name: 'Customize colours…' }).click();
    await dialog.getByRole('button', { name: 'Use Classic’s colours' }).click();
    await expect.poll(background).toBe(classic);
    const choosing = page.waitForEvent('filechooser');
    await dialog.getByRole('button', { name: 'Load from a file…' }).click();
    await (await choosing).setFiles(saved);
    await expect(dialog.getByRole('status')).toHaveText('Loaded Harbour.');
    await expect.poll(background).toBe('rgb(32, 48, 64)');
  });

  test('in Classic, a function key the screen names is clicked like a key, as in ACS', async ({
    page,
  }) => {
    await signIn(page, 'terminal.classic@example.com', '/?look=classic');
    await connect(page, 'Fake IBM i');
    await signOnFake(page);
    // "F3=Exit" is on row 22 from column 2; Classic draws nothing over it.
    const f3 = await cell(page, 22, 3);
    await page.mouse.move(f3.x, f3.y);
    await expect(page.locator('#terminal')).toHaveClass(/over-target/);
    await expect(page.locator('#terminal')).toHaveAttribute('title', 'Exit (F3)');
    await expect(page.locator('#terminal .underlay')).toHaveCount(0);
    // F3 signs off the fake menu, which closes the tab.
    await page.mouse.click(f3.x, f3.y);
    await expect(page.getByRole('tablist', { name: 'Sessions' }).getByRole('tab')).toHaveText([
      'New session',
    ]);
  });

  for (const look of ['classic', 'greenglow'])
    test(`a 27 x 132 screen fills the page and goes back to 24 x 80 (${look})`, async ({
      page,
    }) => {
      await signIn(page, `terminal.wide.${look}@example.com`, `/?look=${look}`);
      await connect(page, 'Fake IBM i');
      await signOnFake(page);
      const terminal = page.locator('#terminal');
      const size = () =>
        terminal.evaluate((t) => [
          getComputedStyle(t).getPropertyValue('--columns').trim(),
          getComputedStyle(t).getPropertyValue('--lines').trim(),
        ]);
      expect(await size()).toEqual(['80', '24']);

      // Option 132 on the fake IBM i's menu sends CLEAR UNIT ALTERNATE.
      await type(page, '132');
      await page.keyboard.press('Enter');
      await expectScreen(page, /Wide Placeholder Screen/);
      expect(await size()).toEqual(['132', '27']);
      const rows = (await screenText(page)).split('\n');
      expect(rows[2]).toMatch(/^ Left edge +Right edge$/);
      expect(rows[2]).toHaveLength(132);
      // The whole width is on the page.
      const grid = await page.locator('#terminal .grid').boundingBox();
      const box = await terminal.boundingBox();
      expect(grid && box && grid.x + grid.width <= box.x + box.width).toBe(true);

      await page.keyboard.press('F3');
      await expectScreen(page, /IBM i Main Menu/);
      expect(await size()).toEqual(['80', '24']);
    });

  test('the password field never shows what was typed', async ({ page }) => {
    await signIn(page, 'terminal2@example.com');
    await connect(page, 'Fake IBM i');
    await type(page, 'X');
    await page.keyboard.press('Tab');
    await page.keyboard.type(FAKE_PASSWORD);
    expect(await screenText(page)).not.toContain(FAKE_PASSWORD);
  });

  test('a dragged block copies as a rectangle', async ({ page, context }) => {
    await context.grantPermissions(['clipboard-read', 'clipboard-write']);
    await signIn(page, 'terminal3@example.com');
    await connect(page, 'Fake IBM i');
    await signOnFake(page);
    // Row 1 holds "MAIN" at column 2: select rows 1-1, columns 2-5.
    const from = await cell(page, 1, 2);
    const to = await cell(page, 1, 5);
    await page.mouse.move(from.x, from.y);
    await page.mouse.down();
    await page.mouse.move(to.x, to.y, { steps: 5 });
    await page.mouse.up();
    await expect(page.locator('#terminal .selection')).toBeVisible();
    await page.keyboard.press('Control+c');
    await expect.poll(() => page.evaluate(() => navigator.clipboard.readText())).toBe('MAIN');
    // A drag selects and leaves the cursor where it was: typing still goes to the command line.
    await type(page, '77');
    await page.keyboard.press('Enter');
    await expectScreen(page, /Option 77 is not valid/);
  });

  test('a plain click moves the cursor', async ({ page }) => {
    await signIn(page, 'terminal4@example.com');
    await connect(page, 'Fake IBM i');
    // The password field's first cell (row 7, column 53).
    const field = await cell(page, 7, 53);
    await page.mouse.click(field.x, field.y);
    await expect(page.locator('#terminal .status')).toContainText('07/053');
  });

  test('in a short window the menu scrolls and stays on screen', async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 480 });
    await signIn(page, 'terminal.short@example.com');
    await connect(page, 'Fake IBM i');
    await page.click('.bar-menu summary');
    const panel = page.locator('.bar-menu-panel');
    await expect(panel).toBeVisible();
    const box = (await panel.boundingBox())!;
    expect(box.y + box.height).toBeLessThanOrEqual(480);
    expect(await panel.evaluate((el) => el.scrollHeight > el.clientHeight)).toBe(true);
    // The last item is still reachable.
    const signOut = panel.getByText('Sign out');
    await signOut.scrollIntoViewIfNeeded();
    await expect(signOut).toBeInViewport();
  });
});
