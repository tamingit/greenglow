import { expect, test, type Page } from '@playwright/test';
import { ADMIN, connect, endSessions, expectScreen, signIn, signOnFake } from './helpers.ts';

async function openAdmin(page: Page, tab: string): Promise<void> {
  await page.click('.bar-menu summary');
  await page.click('#menu-admin');
  await expect(page).toHaveURL(/\/admin\.html/);
  await page.getByRole('tab', { name: tab }).click();
}

test.describe('administration', () => {
  test('only administrators see the page', async ({ page }) => {
    await signIn(page, 'not-an-admin@example.com');
    await page.click('.bar-menu summary');
    await expect(page.locator('#menu-admin')).toBeHidden();
    await page.goto('/admin.html');
    await expect(page).toHaveURL(/\/$/);
  });

  test('the initial administrator adds and removes another', async ({ page }) => {
    await signIn(page, ADMIN);
    await openAdmin(page, 'Administrators');
    const table = page.locator('#panel-admins table');
    await expect(table).toContainText(ADMIN);
    await expect(table).toContainText('initial');

    await page.getByLabel('Add an administrator').fill('second.admin@example.com');
    await page.getByRole('option', { name: /second\.admin@example\.com/ }).click();
    await expect(table).toContainText('second.admin@example.com');
    await expect(table.getByRole('row', { name: /second\.admin/ })).toContainText('Not yet');

    page.once('dialog', (dialog) => void dialog.accept());
    await table
      .getByRole('row', { name: /second\.admin/ })
      .getByRole('button', { name: 'Remove' })
      .click();
    await expect(table).not.toContainText('second.admin@example.com');
  });

  test('an administrator adds a connection and gives a group access', async ({ page }) => {
    await signIn(page, ADMIN);
    await openAdmin(page, 'Connections');
    const add = page.getByRole('region', { name: 'Add a connection' });
    await add.getByLabel(/^Name/).fill('extra');
    await add.getByLabel('Label').fill('Extra system');
    await add.getByLabel('Address').fill('tcp://127.0.0.1:2323');
    await add.getByLabel('Sessions per user').fill('2');
    await add.getByRole('button', { name: 'Add connection' }).click();

    const card = page.getByRole('region', { name: 'Extra system' });
    await expect(card).toContainText('No one yet');
    await expect(card).toContainText('not encrypted');
    await card.getByLabel('Add a group').fill('Dev');
    await page.getByRole('option', { name: 'Developers' }).click();
    await expect(card.getByRole('list', { name: /Who may use/ })).toContainText(
      'Group: Developers',
    );

    await card.getByLabel('Sessions per user').fill('5');
    await card.getByRole('button', { name: 'Save' }).click();
    await expect(page.locator('#admin-message')).toContainText('Saved Extra system');
    await page.reload();
    await expect(
      page.getByRole('region', { name: 'Extra system' }).getByLabel('Sessions per user'),
    ).toHaveValue('5');

    page.once('dialog', (dialog) => void dialog.accept());
    await page
      .getByRole('region', { name: 'Extra system' })
      .getByRole('button', { name: 'Delete' })
      .click();
    await expect(page.getByRole('region', { name: 'Extra system' })).toHaveCount(0);
  });

  test('an administrator changes a session limit and goes back to the default', async ({
    page,
  }) => {
    await signIn(page, ADMIN);
    await openAdmin(page, 'Settings');
    const card = page.locator('.admin-card', { hasText: 'Minutes kept with no browser' });
    await card.getByLabel('Value').fill('7');
    await card.getByRole('button', { name: 'Save' }).click();
    const tag = card.locator('.admin-tag', { hasText: 'default' });
    await expect(tag).toHaveCount(0);
    await expect(card.getByLabel('Value')).toHaveValue('7');
    await card.getByRole('button', { name: 'Use the default' }).click();
    await expect(tag).toHaveCount(1);
    await expect(card.getByLabel('Value')).toHaveValue('5');
  });

  test('the connection log shows current connections and downloads as CSV', async ({
    page,
    context,
  }) => {
    await signIn(page, ADMIN);
    // The terminal stays open in its own tab, so its browser row stays current.
    const terminal = await context.newPage();
    await terminal.goto('/');
    await connect(terminal, 'Fake IBM i');
    await openAdmin(page, 'Connection log');
    const log = page.getByRole('table', { name: 'Connection log' });
    const current = log
      .getByRole('row')
      .filter({ hasText: 'Host session' })
      .filter({ hasText: 'current' });
    await expect(current.first()).toContainText(ADMIN);
    await expect(current.first()).toContainText('fake');
    await page.getByLabel('Current only').check();
    await expect(log.getByRole('row').filter({ hasText: 'Browser (owner)' }).first()).toBeVisible();

    const download = page.waitForEvent('download');
    await page.getByRole('button', { name: 'Download CSV' }).click();
    const file = await download;
    expect(file.suggestedFilename()).toMatch(/^greenglow-connection-log-\d{4}-\d{2}-\d{2}\.csv$/);
  });

  test('the Nodes tab lists this server and its sessions, and a drain stops new ones', async ({
    page,
    context,
    browser,
  }) => {
    await signIn(page, ADMIN);
    const terminal = await context.newPage();
    await terminal.goto('/');
    await connect(terminal, 'Fake IBM i');
    await openAdmin(page, 'Nodes');
    // npm run e2e runs the server as node 7.
    const node = page.getByRole('region', { name: 'Node 7' });
    await expect(node).toContainText('this one');
    await expect(node).toContainText('active');
    // Earlier tests may have left more of the administrator's sessions open.
    await expect(node.getByRole('row').filter({ hasText: ADMIN }).first()).toContainText('GG7');

    const origin = new URL(page.url()).origin;
    try {
      page.once('dialog', (dialog) => void dialog.accept());
      await node.getByRole('button', { name: 'Drain' }).click();
      await expect(node).toContainText('draining');

      // Someone opening a session now is told to open it again, to use another server.
      const other = await browser.newPage();
      await signIn(other, 'drain.tester@example.com');
      await other
        .locator('#terminal .offline')
        .getByRole('button', { name: /^(Connect to )?Fake IBM i/ })
        .first()
        .click();
      await expect(
        other.getByRole('alertdialog', { name: 'This server is restarting' }),
      ).toBeVisible();
      await other.close();
      // The session already open carries on.
      await expectScreen(terminal, /Sign On|User/);

      await node.getByRole('button', { name: 'Resume' }).click();
      await expect(node).toContainText('active');
    } finally {
      await page.request.post('/api/admin/nodes/7/drain', {
        data: { drain: false },
        headers: { origin },
      });
      await endSessions(terminal);
    }
  });

  test('a certificate is checked, and trusting one for another name asks first', async ({
    page,
  }) => {
    // The e2e setup has no IBM i with TLS: the server's answers are stood in for here; the
    // server side is tested in CertificateTrustTests.
    await signIn(page, ADMIN);
    await openAdmin(page, 'Connections');
    const add = page.getByRole('region', { name: 'Add a connection' });
    await add.getByLabel(/^Name/).fill('tls-trust');
    await add.getByLabel('Label').fill('TLS trust');
    await add.getByLabel('Address').fill('tls://127.0.0.1:1');
    await add.getByRole('button', { name: 'Add connection' }).click();
    const card = page.getByRole('region', { name: 'TLS trust' });
    const root = 'AB'.repeat(32);
    await page.route('**/certificate/check', (route) =>
      route.fulfill({
        json: {
          certificates: [
            {
              subject: 'CN=ibmi.example.com',
              issuer: 'CN=Example Root',
              notBefore: '2026-01-01T00:00:00Z',
              notAfter: '2027-01-01T00:00:00Z',
              sha256: 'CD'.repeat(32),
              selfSigned: false,
              isCa: false,
            },
            {
              subject: 'CN=Example Root',
              issuer: 'CN=Example Root',
              notBefore: '2020-01-01T00:00:00Z',
              notAfter: '2040-01-01T00:00:00Z',
              sha256: root,
              selfSigned: true,
              isCa: true,
            },
          ],
          names: ['ibmi.example.com'],
          host: '127.0.0.1',
          hostMatches: false,
          trustable: root,
          verified: false,
        },
      }),
    );
    const sent: unknown[] = [];
    await page.route('**/certificate/trust', async (route) => {
      const body = route.request().postDataJSON() as { acceptName?: string };
      sent.push(body);
      await (body.acceptName
        ? route.fulfill({ json: {} })
        : route.fulfill({
            status: 409,
            json: {
              error: 'name-mismatch',
              names: ['ibmi.example.com'],
              message: 'The certificate is for ibmi.example.com, not 127.0.0.1.',
            },
          }));
    });
    await card.getByRole('button', { name: 'Check certificate' }).click();
    const chain = card.getByRole('list', { name: 'Certificates the IBM i sent' });
    await expect(chain.getByRole('listitem')).toHaveCount(2);
    await expect(chain).toContainText(`SHA-256 ${'AB:'.repeat(31)}AB`);
    await expect(card).toContainText('It is for ibmi.example.com, not 127.0.0.1.');

    let asked = '';
    page.once('dialog', (dialog) => {
      asked = dialog.message();
      void dialog.accept();
    });
    await card.getByRole('button', { name: 'Trust CN=Example Root' }).click();
    await expect(page.locator('#admin-message')).toContainText(
      'TLS trust now trusts CN=Example Root',
    );
    expect(asked).toContain('Accept it anyway?');
    expect(sent).toEqual([{ sha256: root }, { sha256: root, acceptName: 'ibmi.example.com' }]);

    await page.unrouteAll();
    page.once('dialog', (dialog) => void dialog.accept());
    await page
      .getByRole('region', { name: 'TLS trust' })
      .getByRole('button', { name: 'Delete' })
      .click();
  });

  test('going to the admin page and back brings the session back', async ({ page }) => {
    await signIn(page, ADMIN);
    await endSessions(page);
    await page.goto('/');
    await connect(page, 'Fake IBM i');
    await signOnFake(page);
    const terminal = page.locator('#terminal .offline');

    // By the admin page's link, and by the browser's Back button.
    await openAdmin(page, 'Connections');
    await page.getByRole('link', { name: 'Back to the terminal' }).first().click();
    await expectScreen(page, /IBM i Main Menu/);
    await expect(terminal).toBeHidden();
    await openAdmin(page, 'Connections');
    await page.goBack();
    await expectScreen(page, /IBM i Main Menu/);
    await expect(terminal).toBeHidden();
    const response = await page.request.get('/api/sessions');
    const { sessions } = (await response.json()) as { sessions: { frontends: number }[] };
    expect(sessions.map((s) => s.frontends)).toEqual([1]);
  });

  test('without a CA, a tls:// connection is marked unverified here and on the terminal', async ({
    page,
  }) => {
    await signIn(page, ADMIN);
    await openAdmin(page, 'Connections');
    const add = page.getByRole('region', { name: 'Add a connection' });
    await add.getByLabel(/^Name/).fill('tls-check');
    await add.getByLabel('Label').fill('TLS check');
    await add.getByLabel('Address').fill('tls://127.0.0.1:1');
    await add.getByRole('button', { name: 'Add connection' }).click();
    await expect(page.getByRole('region', { name: 'TLS check' })).toContainText(
      'certificate not verified',
    );
    // Its certificate section says so, and a check of an address with no TLS says why it failed.
    const card = page.getByRole('region', { name: 'TLS check' });
    await expect(card).toContainText('Not verified: any certificate is accepted.');
    await card.getByRole('button', { name: 'Check certificate' }).click();
    await expect(page.locator('#admin-message')).toContainText(
      "Couldn't read the IBM i's certificate",
    );

    await endSessions(page);
    await page.goto('/');
    // The Disconnected card's tiles say how each is protected.
    const systems = page.locator('#terminal .offline').getByRole('group', { name: 'Systems' });
    await expect(systems.getByRole('button', { name: /^TLS check/ })).toContainText(
      'TLS, unverified',
    );
    await expect(systems.getByRole('button', { name: /^Fake IBM i/ })).toContainText(
      'Not encrypted',
    );

    await page.goto('/admin.html#connections');
    page.once('dialog', (dialog) => void dialog.accept());
    await page
      .getByRole('region', { name: 'TLS check' })
      .getByRole('button', { name: 'Delete' })
      .click();
    await expect(page.getByRole('region', { name: 'TLS check' })).toHaveCount(0);
  });
});
