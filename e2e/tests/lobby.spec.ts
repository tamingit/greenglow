import { expect, test, type Page } from '@playwright/test';
import {
  ADMIN,
  FAKE_USER,
  connect,
  disconnect,
  endSessions,
  expectScreen,
  signIn,
  signOnFake,
} from './helpers.ts';

/** Signs on, then leaves the session running on the server. */
async function leaveRunning(page: Page): Promise<void> {
  await connect(page, 'Fake IBM i');
  await signOnFake(page);
  await disconnect(page);
  await page.getByRole('alertdialog').getByRole('button', { name: 'Leave it running' }).click();
}

test.describe('lobby', () => {
  test('a session left running is listed and can be joined again', async ({ page }) => {
    await signIn(page, 'lobby.join@example.com');
    await leaveRunning(page);
    const table = page.getByRole('table', { name: 'Your open session' });
    const row = table.getByRole('row').nth(1);
    await expect(row).toContainText('Fake IBM i');
    await expect(row).toContainText(FAKE_USER);
    await expect(row).toContainText('IBM i Main Menu');
    await expect(row).toContainText('0 of 2');
    await row.getByRole('button', { name: 'Join' }).click();
    await expectScreen(page, /IBM i Main Menu/);
    await expect(table).toBeHidden();
    // It sends no startup reply, so the first screen ends Joining in the status.
    await expect(page.locator('#status-text')).toHaveText('Fake IBM i: joined your session.');
  });

  test('a session left running is joined again when the page loads', async ({ page }) => {
    await signIn(page, 'lobby.load@example.com');
    await leaveRunning(page);
    await page.reload();
    await expectScreen(page, /IBM i Main Menu/);
    await expect(page.locator('#status-text')).toHaveText('Fake IBM i: joined your session.');
  });

  test('a reload brings back the sessions it showed', async ({ page }) => {
    await signIn(page, 'lobby.reload@example.com');
    await connect(page, 'Fake IBM i');
    await signOnFake(page);
    await page.reload();
    await expectScreen(page, /IBM i Main Menu/);
    const response = await page.request.get('/api/sessions');
    const { sessions } = (await response.json()) as { sessions: { frontends: number }[] };
    expect(sessions.map((s) => s.frontends)).toEqual([1]);
  });

  test('the list follows sessions as they change elsewhere, without polling', async ({ page }) => {
    await signIn(page, 'lobby.push@example.com');
    let asked = 0;
    page.on('request', (request) => {
      if (new URL(request.url()).pathname === '/api/sessions' && request.method() === 'GET')
        asked++;
    });
    await leaveRunning(page);
    const table = page.getByRole('table', { name: 'Your open session' });
    await expect(table.getByRole('row').nth(1)).toContainText('IBM i Main Menu');

    // Ended from elsewhere: the row goes at once.
    const origin = new URL(page.url()).origin;
    const { sessions } = (await (await page.request.get('/api/sessions')).json()) as {
      sessions: { id: string }[];
    };
    await page.request.delete(`/api/sessions/${sessions[0].id}`, { headers: { origin } });
    await expect(table).toBeHidden({ timeout: 3_000 });

    // Nothing asks for the list over time: the server pushes it.
    asked = 0;
    await page.waitForTimeout(11_000);
    expect(asked).toBe(0);
  });

  test('a session can be ended from the list', async ({ page }) => {
    await signIn(page, 'lobby.end@example.com');
    await leaveRunning(page);
    const row = page.getByRole('table', { name: 'Your open session' }).getByRole('row').nth(1);
    await row.getByRole('button', { name: 'End' }).click();
    await expect(page.getByRole('table', { name: /Your open session/ })).toBeHidden();
    const response = await page.request.get('/api/sessions');
    expect(((await response.json()) as { sessions: unknown[] }).sessions).toHaveLength(0);
  });

  test('a session already showing in two browsers can only be taken over by a third', async ({
    page,
    context,
  }) => {
    await signIn(page, 'lobby.full@example.com');
    await connect(page, 'Fake IBM i');
    await signOnFake(page);
    const second = await context.newPage();
    await second.goto('/');
    await second
      .getByRole('table', { name: 'Your open session' })
      .getByRole('button', { name: 'Join' })
      .click();
    await expectScreen(second, /IBM i Main Menu/);

    const third = await context.newPage();
    await third.goto('/');
    const row = third.getByRole('table', { name: 'Your open session' }).getByRole('row').nth(1);
    await expect(row).toContainText('2 of 2');
    await expect(row.getByRole('button', { name: 'Join' })).toHaveCount(0);
    await expect(row.getByRole('button', { name: 'Take over' })).toBeEnabled();
  });

  test('with one browser per session, a session in use elsewhere can be taken over', async ({
    page,
    context,
  }) => {
    await signIn(page, ADMIN);
    await endSessions(page);
    const origin = new URL(page.url()).origin;
    const limit = (value: number | null) =>
      page.request.put('/api/admin/settings/Sessions.MaxFrontends', {
        data: { value },
        headers: { origin },
      });
    expect((await limit(1)).ok()).toBe(true);
    try {
      await page.goto('/');
      await connect(page, 'Fake IBM i');
      await signOnFake(page);

      const other = await context.newPage();
      await other.goto('/');
      const table = other.getByRole('table', { name: 'Your open session' });
      await expect(table.getByRole('columnheader', { name: 'Status' })).toBeVisible();
      const row = table.getByRole('row').nth(1);
      await expect(row).toContainText('In use');
      await row.getByRole('button', { name: 'Take over' }).click();
      await expectScreen(other, /IBM i Main Menu/);
      await expect(page.getByRole('alertdialog', { name: 'Opened elsewhere' })).toBeVisible();
    } finally {
      await limit(null);
      await endSessions(page);
    }
  });

  test('a long screen title wraps, and the buttons stay whole', async ({ page }) => {
    // The list as the server would send it, with a screen title as long as the server keeps
    // (80 characters).
    const title =
      'Work with Placeholder Entry Header Maintenance and Placeholder Instructions  ABC';
    const sessions = ['a', 'b'].map((id) => ({
      id: `00000000-0000-0000-0000-00000000000${id === 'a' ? 1 : 2}`,
      target: 'fake',
      label: 'Fake IBM i',
      secure: false,
      createdAt: new Date().toISOString(),
      device: null,
      ibmUser: 'TESTER',
      title,
      // Showing elsewhere, so the page lists them rather than joining them.
      frontends: 1,
      ownerFrontends: 1,
      ownerPresent: true,
      signedOn: true,
      owner: 'lobby.long@example.com',
      role: 'owner',
    }));
    await page.route('**/api/sessions', (route) =>
      route.request().method() === 'GET' ? route.fulfill({ json: { sessions } }) : route.fallback(),
    );
    // The page's feed, which brings the list.
    await page.routeWebSocket('**/ws/user', (ws) =>
      ws.send(JSON.stringify({ type: 'sessions', sessions })),
    );
    await page.setViewportSize({ width: 900, height: 800 });
    await signIn(page, 'lobby.long@example.com');
    const table = page.getByRole('table', { name: 'Your open sessions' });
    await expect(table).toContainText(title);
    for (const button of await table.getByRole('button').all()) {
      const fits = await button.evaluate(
        (b) => b.scrollWidth <= b.clientWidth && b.getBoundingClientRect().height < 40,
      );
      expect(fits, await button.innerText()).toBe(true);
    }
  });
});
