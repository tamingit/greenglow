import { expect, test, type Browser, type BrowserContext, type Page } from '@playwright/test';
import { connect, disconnect, expectScreen, signIn, signOnFake, type } from './helpers.ts';

/** The other people's browser contexts, closed after each test so they stop polling. */
const others: BrowserContext[] = [];

/** A page for another person, with their own sign-in. */
async function person(browser: Browser, upn: string): Promise<Page> {
  const context = await browser.newContext();
  others.push(context);
  const page = await context.newPage();
  await signIn(page, upn);
  return page;
}

/** The owner shares their session with people at an access level, and returns the link. */
async function share(
  owner: Page,
  people: [string, 'View-Only' | 'Full Access'][],
): Promise<string> {
  await owner.click('.bar-menu summary');
  await owner.click('#menu-share');
  const dialog = owner.getByRole('dialog', { name: /Share your session/ });
  for (const [upn, access] of people) {
    await dialog.getByLabel('Add a person').fill(upn);
    await owner.getByRole('option', { name: new RegExp(upn.replace('.', '\\.')) }).click();
    await dialog.getByLabel(`Access for ${upn}`).selectOption({ label: access });
  }
  await dialog.getByRole('button', { name: 'Create link' }).click();
  const link = await dialog.getByLabel('Share link').inputValue();
  expect(link).toContain('/?share=');
  return link;
}

test.describe('sharing', () => {
  test.afterEach(async () => {
    await Promise.all(others.splice(0).map((context) => context.close()));
  });

  test('view-only watches; full access takes the keyboard; revoking sends them away', async ({
    page,
    browser,
  }) => {
    test.setTimeout(60_000);
    await signIn(page, 'share.owner@example.com');
    await connect(page, 'Fake IBM i');
    await signOnFake(page);
    const link = await share(page, [
      ['share.viewer@example.com', 'View-Only'],
      ['share.helper@example.com', 'Full Access'],
    ]);
    await page.keyboard.press('Escape');

    const viewer = await person(browser, 'share.viewer@example.com');
    await viewer.goto(link);
    await expectScreen(viewer, /IBM i Main Menu/);
    await expect(viewer.locator('#control-text')).toHaveText('View only');
    await expect(viewer.locator('#take-control')).toBeHidden();
    await type(viewer, 'x');
    await expect(viewer.locator('#status-text')).toContainText('View only');

    const helper = await person(browser, 'share.helper@example.com');
    // The session also shows in the helper's own list.
    const row = helper.getByRole('table', { name: 'Your open session' }).getByRole('row').nth(1);
    await expect(row).toContainText('shared by share.owner@example.com');
    // People it is shared with don't count toward the browsers-per-session limit (2): the
    // owner and the viewer are in, and the helper still gets in.
    await expect(row.getByRole('button', { name: 'Join' })).toBeEnabled();
    await row.getByRole('button', { name: 'Join' }).click();
    await expectScreen(helper, /IBM i Main Menu/);
    await expect(helper.locator('#status-text')).toHaveText(
      'Fake IBM i: joined share.owner@example.com’s session (full access).',
    );
    await expect(helper.locator('#control-text')).toContainText('has the keyboard');
    await helper.click('#take-control');
    await expect(helper.locator('#control-text')).toHaveText('You have the keyboard');
    await expect(page.locator('#control-text')).toContainText('share.helper has the keyboard');
    await type(helper, '77');
    await helper.keyboard.press('Enter');
    await expectScreen(page, /Option 77 is not valid/);

    // The owner types while the helper has the keyboard, is told so, then takes it back:
    // the status line must follow, not keep the old warning.
    await type(page, 'x');
    await expect(page.locator('#status-text')).toContainText(
      'has the keyboard. Take control to type.',
    );
    await page.click('#take-control');
    await expect(page.locator('#control-text')).toHaveText('You have the keyboard');
    await expect(page.locator('#status-text')).toHaveText('You have the keyboard.');
    await expect(helper.locator('#status-text')).toHaveText(
      'share.owner has the keyboard. Take control to type.',
    );

    await page.click('.bar-menu summary');
    await page.click('#menu-share');
    const dialog = page.getByRole('dialog', { name: /Share your session/ });
    await dialog.getByRole('button', { name: 'Revoke' }).click();
    await expect(helper.getByRole('alertdialog', { name: 'Sharing stopped' })).toContainText(
      'The owner stopped sharing this session.',
    );
    await expect(helper.locator('#status-text')).toHaveText('Sharing stopped.');
  });

  test('the owner sees who it is shared with, makes one view-only and removes them', async ({
    page,
    browser,
  }) => {
    test.setTimeout(60_000);
    await signIn(page, 'list.owner@example.com');
    await connect(page, 'Fake IBM i');
    await signOnFake(page);
    const link = await share(page, [
      ['list.helper@example.com', 'Full Access'],
      ['list.other@example.com', 'View-Only'],
    ]);
    await page.keyboard.press('Escape');

    const helper = await person(browser, 'list.helper@example.com');
    await helper.goto(link);
    await expectScreen(helper, /IBM i Main Menu/);
    await helper.click('#take-control');
    await expect(helper.locator('#control-text')).toHaveText('You have the keyboard');

    // From the menu: each person, their access, and who is watching now.
    await page.click('.bar-menu summary');
    await page.click('#menu-share');
    const dialog = page.getByRole('dialog', { name: /Share your session/ });
    const people = dialog.getByRole('list', { name: 'Shared with', exact: true });
    const helperRow = people.locator('[data-upn="list.helper@example.com"]');
    const otherRow = people.locator('[data-upn="list.other@example.com"]');
    await expect(helperRow).toContainText('Watching now');
    await expect(otherRow).not.toContainText('Watching now');
    await expect(otherRow.getByLabel('Access for list.other@example.com')).toHaveValue('view');

    // View-only at once: the helper loses the keyboard and is told why.
    await helperRow.getByLabel('Access for list.helper@example.com').selectOption('view');
    await expect(helper.locator('#control-text')).toHaveText('View only');
    await expect(helper.locator('#take-control')).toBeHidden();
    await expect(helper.locator('#status-text')).toHaveText(
      'The owner changed your access to view only.',
    );
    await expect(page.locator('#control-text')).toHaveText('You have the keyboard');

    // Removing one person sends only them away.
    await helperRow
      .getByRole('button', { name: 'Stop sharing with list.helper@example.com' })
      .click();
    await expect(helper.getByRole('alertdialog', { name: 'Sharing stopped' })).toContainText(
      'The owner stopped sharing this session with you.',
    );
    await expect(helperRow).toHaveCount(0);
    await expect(otherRow).toBeVisible();
  });

  test('the share dialog closes from its corner or with a click off it', async ({ page }) => {
    await signIn(page, 'share.closer@example.com');
    await connect(page, 'Fake IBM i');
    const dialog = page.getByRole('dialog', { name: /Share your session/ });
    const open = async () => {
      await page.click('.bar-menu summary');
      await page.click('#menu-share');
      await expect(dialog).toBeVisible();
    };

    await open();
    await dialog.getByRole('button', { name: 'Close', exact: true }).click();
    await expect(dialog).toHaveCount(0);

    // A click inside the dialog, even on its padding, keeps it.
    await open();
    const box = (await dialog.boundingBox())!;
    await page.mouse.click(box.x + 4, box.y + box.height / 2);
    await expect(dialog).toBeVisible();
    // A click on the backdrop closes it.
    await page.mouse.click(box.x / 2, box.y + box.height / 2);
    await expect(dialog).toHaveCount(0);
  });

  test('a session shared with someone already in the page pops up for them to join', async ({
    page,
    browser,
  }) => {
    test.setTimeout(60_000);
    await signIn(page, 'invite.owner@example.com');
    await connect(page, 'Fake IBM i');
    await signOnFake(page);
    // The helper is busy in a session of their own.
    const helper = await person(browser, 'invite.helper@example.com');
    await connect(helper, 'Fake IBM i');
    await expectScreen(helper, /Sign On/);

    await share(page, [['invite.helper@example.com', 'Full Access']]);
    await page.keyboard.press('Escape');
    const card = helper.locator('.invite');
    // Pushed at once (/ws/user), not found by a poll.
    await expect(card).toHaveText(
      /invite\.owner@example\.com shared their session on Fake IBM i with you \(full access\)/,
      { timeout: 3_000 },
    );
    // It never takes the keys from the screen.
    await expect(helper.locator('#terminal')).toBeFocused();
    await expect(card.getByRole('button', { name: 'Join' })).toBeVisible();

    // Alt+J joins it in a tab of its own, beside the helper's session.
    await helper.keyboard.press('Alt+J');
    await expect(card).toHaveCount(0);
    await expectScreen(helper, /IBM i Main Menu/);
    await expect(helper.getByRole('tablist', { name: 'Sessions' }).getByRole('tab')).toHaveCount(2);
  });

  test('an offer goes away when the session is no longer shared', async ({ page, browser }) => {
    test.setTimeout(60_000);
    await signIn(page, 'invite.owner2@example.com');
    await connect(page, 'Fake IBM i');
    const watcher = await person(browser, 'invite.watcher@example.com');
    await share(page, [['invite.watcher@example.com', 'View-Only']]);
    const card = watcher.locator('.invite');
    await expect(card.getByRole('button', { name: 'Watch' })).toBeVisible({ timeout: 15_000 });
    await page
      .getByRole('dialog', { name: /Share your session/ })
      .getByRole('button', { name: 'Revoke link' })
      .click();
    await expect(card).toHaveCount(0, { timeout: 15_000 });
  });

  test('when the owner disconnects, the people it is shared with lose it', async ({
    page,
    browser,
  }) => {
    test.setTimeout(60_000);
    await signIn(page, 'share.leaver@example.com');
    await connect(page, 'Fake IBM i');
    await signOnFake(page);
    const link = await share(page, [['share.left@example.com', 'Full Access']]);
    await page.keyboard.press('Escape');
    const helper = await person(browser, 'share.left@example.com');
    await helper.goto(link);
    await expectScreen(helper, /IBM i Main Menu/);
    // The Share panel counts them once the page knows.
    await page.locator('.side-tab', { hasText: 'Share' }).click();
    await expect(
      page
        .getByRole('complementary', { name: 'Sharing' })
        .getByRole('list', { name: 'Shared with', exact: true }),
    ).toContainText('share.left');

    await disconnect(page);
    const dialog = page.getByRole('alertdialog');
    await expect(dialog).toContainText(
      'The person it’s shared with loses it until you’re back in it.',
    );
    await dialog.getByRole('button', { name: 'Leave it running' }).click();
    const notice = helper.getByRole('alertdialog', { name: 'Owner left' });
    await expect(notice).toContainText('You can join it again once they are back in it.');
  });

  test('the sharing panel shows each person, their access and the keyboard', async ({
    page,
    browser,
  }) => {
    test.setTimeout(90_000);
    await signIn(page, 'panel.owner@example.com');
    await connect(page, 'Fake IBM i');
    await signOnFake(page);
    const link = await share(page, [
      ['panel.helper@example.com', 'Full Access'],
      ['panel.viewer@example.com', 'View-Only'],
    ]);
    await page.keyboard.press('Escape');
    const helper = await person(browser, 'panel.helper@example.com');

    // Alt+S opens the panel beside the screen.
    await page.locator('#terminal').focus();
    await page.keyboard.press('Alt+S');
    const panel = page.getByRole('complementary', { name: 'Sharing' });
    const people = panel.getByRole('list', { name: 'Shared with', exact: true });
    const helperRow = people.locator('[data-upn="panel.helper@example.com"]');
    const viewerRow = people.locator('[data-upn="panel.viewer@example.com"]');
    // The helper has GreenGlow open but hasn't joined; the viewer hasn't signed in at all.
    await expect(helperRow).toHaveAttribute('data-state', 'near', { timeout: 20_000 });
    await expect(viewerRow).toHaveAttribute('data-state', 'off');
    await expect(viewerRow.locator('.sharing-key')).toHaveAttribute('data-state', 'view');

    await helper.goto(link);
    await expectScreen(helper, /IBM i Main Menu/);
    await expect(helperRow).toHaveAttribute('data-state', 'on');
    await helper.click('#take-control');
    await expect(helperRow.locator('.sharing-key')).toHaveAttribute('data-state', 'keyboard');
    await expect(panel.getByRole('button', { name: 'Take control' })).toBeVisible();

    // The helper gives it back from their own panel, which lists the session as theirs to leave.
    await helper.locator('#terminal').focus();
    await helper.keyboard.press('Alt+S');
    const helperPanel = helper.getByRole('complementary', { name: 'Sharing' });
    await expect(
      helperPanel
        .getByRole('list', { name: 'Sessions shared with you' })
        .getByRole('button', { name: /^Leave panel\.owner/ }),
    ).toBeVisible({ timeout: 15_000 });
    // The helper sees who else it is shared with, the owner first, and can change none of it.
    const others = helperPanel.getByRole('list', { name: 'Shared with', exact: true });
    await expect(others.getByRole('listitem')).toHaveCount(3);
    await expect(others.getByRole('listitem').first()).toContainText('Owner');
    await expect(others).toContainText('panel.viewer');
    await expect(others).toContainText('(you)');
    await expect(others.getByRole('button')).toHaveCount(0);
    await expect(helperPanel.getByRole('button', { name: 'Share with someone' })).toBeHidden();
    await helperPanel.getByRole('button', { name: 'Return control' }).click();
    await expect(page.locator('#control-text')).toHaveText('You have the keyboard');

    // The owner makes the helper view-only with the keyboard button, and stops sharing with the
    // viewer with ×.
    await helperRow.locator('.sharing-key').click();
    await expect(helper.locator('#control-text')).toHaveText('View only');
    await expect(helperRow.locator('.sharing-key')).toHaveAttribute('data-state', 'view');
    await viewerRow
      .getByRole('button', { name: 'Stop sharing with panel.viewer@example.com' })
      .click();
    await expect(viewerRow).toHaveCount(0);

    // The helper takes the session off their list: they leave it, and the owner's list empties.
    await helperPanel
      .getByRole('button', { name: /^Take panel\.owner@example\.com’s session .* off your list$/ })
      .click();
    await expect(helper.getByRole('alertdialog', { name: 'Sharing stopped' })).toBeVisible();
    await expect(helperRow).toHaveCount(0, { timeout: 15_000 });
  });

  test('the owner’s reload brings back a session someone is watching', async ({
    page,
    browser,
  }) => {
    test.setTimeout(60_000);
    await signIn(page, 'reload.owner@example.com');
    await connect(page, 'Fake IBM i');
    await signOnFake(page);
    const link = await share(page, [['reload.watcher@example.com', 'View-Only']]);
    await page.keyboard.press('Escape');
    const watcher = await person(browser, 'reload.watcher@example.com');
    await watcher.goto(link);
    await expectScreen(watcher, /IBM i Main Menu/);

    await page.reload();
    await expectScreen(page, /IBM i Main Menu/);
    await expect(page.locator('#terminal .offline')).toBeHidden();
    // Back within the grace, so the watcher stayed.
    await expect(watcher.getByRole('alertdialog')).toHaveCount(0);
    await expect(watcher.locator('#control-text')).toHaveText('View only');
  });

  test('a link opened by someone it doesn’t name opens nothing', async ({ page, browser }) => {
    await signIn(page, 'share.owner2@example.com');
    await connect(page, 'Fake IBM i');
    const link = await share(page, [['share.named@example.com', 'View-Only']]);
    const stranger = await person(browser, 'share.stranger@example.com');
    await stranger.goto(link);
    await expect(stranger.locator('#status-text')).toContainText(/isn.t for you/);
    await expect(stranger.getByRole('table', { name: /Your open session/ })).toHaveCount(0);
  });
});
