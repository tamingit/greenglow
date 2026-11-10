import { expect, test, type Page } from '@playwright/test';
import { ADMIN, endSessions, expectScreen, signIn } from './helpers.ts';

// The fake IBM i's Network Print server (server/tests/GreenGlow.FakeHost): placeholder
// spooled files for this one user.
const PRINT_USER = 'PRTUSER';
const PRINT_PASSWORD = 'Secret12';

async function printerPanel(page: Page) {
  await page.keyboard.press('Alt+P');
  const panel = page.getByRole('complementary', { name: 'Printer output' });
  await expect(panel).toBeVisible();
  return panel;
}

test.describe('printer output', () => {
  test('signs on, lists the spooled files, opens one as a PDF and signs off', async ({
    page,
    context,
  }) => {
    await signIn(page, 'printer.user@example.com');
    const panel = await printerPanel(page);
    await expect(panel).toContainText('Fake IBM i');

    // A wrong password is refused with the IBM i's words, and the field is empty again.
    await panel.getByLabel('IBM i user').fill(PRINT_USER);
    await panel.getByLabel('Password').fill('not-it');
    await panel.getByRole('button', { name: 'Sign on' }).click();
    await expect(panel.getByRole('status')).toContainText('Wrong password');
    await expect(panel.getByLabel('Password')).toHaveValue('');

    await panel.getByLabel('Password').fill(PRINT_PASSWORD);
    await panel.getByLabel('Password').press('Enter');
    const files = panel.getByRole('list', { name: 'Your spooled files' });
    await expect(files.getByRole('button')).toHaveCount(3);
    await expect(panel).toContainText(`Signed on as ${PRINT_USER}`);
    // Newest first.
    await expect(files.locator('strong')).toHaveText(['QPDFOUT', 'QPRTAFP', 'QSYSPRT']);

    // AFP can't be shown here: no tab opens, and the panel says to use ACS.
    const afp = files.getByRole('button', { name: /QPRTAFP/ });
    await expect(afp).toHaveAttribute('aria-disabled', 'true');
    await afp.click({ force: true });
    await expect(panel.getByRole('status')).toContainText('open it in ACS');

    // The report opens as a PDF in a new tab, from the keyboard.
    await files.getByRole('button', { name: /QPDFOUT/ }).focus();
    await page.keyboard.press('End');
    await expect(files.getByRole('button', { name: /QSYSPRT/ })).toBeFocused();
    const opening = context.waitForEvent('page');
    const pdf = context.waitForEvent('response', (r) =>
      r.url().includes('/api/printer/fake/files/'),
    );
    await page.keyboard.press('Enter');
    await opening;
    const response = await pdf;
    expect(response.status()).toBe(200);
    expect(response.headers()['content-type']).toBe('application/pdf');

    await panel.getByRole('button', { name: 'Sign off' }).click();
    await expect(panel.getByRole('button', { name: 'Sign on' })).toBeVisible();
    await expect(panel.getByRole('status')).toHaveText('Signed off.');

    // Alt+P pushes the panel back.
    await page.keyboard.press('Alt+P');
    await expect(panel).toBeHidden();
  });

  test('the list belongs to its system: another system asks to sign on, the same one does not', async ({
    page,
  }) => {
    // An administrator has both e2e systems: Fake IBM i and Developers only.
    await signIn(page, ADMIN);
    await endSessions(page);
    await page.goto('/');
    await page
      .locator('#terminal .offline')
      .getByRole('button', { name: /^Fake IBM i/ })
      .click();
    await expectScreen(page, /Sign On/);
    const panel = await printerPanel(page);
    await panel.getByLabel('IBM i user').fill(PRINT_USER);
    await panel.getByLabel('Password').fill(PRINT_PASSWORD);
    await panel.getByLabel('Password').press('Enter');
    const files = panel.getByRole('list', { name: 'Your spooled files' });
    await expect(files.getByRole('button')).toHaveCount(3);

    // A new tab on the other system: its tile connects, and the panel moves with it.
    await page.keyboard.press('Alt+N');
    await page
      .locator('#terminal .offline')
      .getByRole('button', { name: /^Developers only/ })
      .click();
    await expectScreen(page, /Sign On/);
    await expect(panel).toContainText('Developers only');
    await expect(panel.getByRole('button', { name: 'Sign on' })).toBeVisible();
    await expect(files).toBeHidden();

    // Back on the first system's tab: the list again, without signing on.
    await page.keyboard.press('Alt+1');
    await expect(panel).toContainText('Fake IBM i');
    await expect(files.getByRole('button')).toHaveCount(3);

    await panel.getByRole('button', { name: 'Sign off' }).click();
    await endSessions(page);
  });

  test('a remembered Printer panel never reopens on its own, so it never asks for the password on load', async ({
    page,
  }) => {
    await signIn(page, 'printer.reload@example.com');
    // The user opens the panel (Alt+P), so it is remembered as the last-open side panel.
    const panel = await printerPanel(page);
    await expect(panel.getByLabel('Password')).toBeVisible();

    await page.reload();
    await expect(page.locator('#terminal .offline')).toBeVisible();

    // Printer was last open, but it must not reopen itself: the password is never asked for
    // until the user clicks Printer again.
    await expect(page.locator('#printer-panel')).toBeHidden();

    // It still opens on demand, and only then asks.
    await page.getByRole('button', { name: 'Printer', exact: true }).click();
    await expect(
      page.getByRole('complementary', { name: 'Printer output' }).getByLabel('Password'),
    ).toBeVisible();
  });
});
