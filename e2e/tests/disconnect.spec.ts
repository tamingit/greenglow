import { expect, test, type Page } from '@playwright/test';
import { connect, disconnect, signIn, signOnFake, type } from './helpers.ts';

interface Listed {
  id: string;
  frontends: number;
}

async function sessions(page: Page): Promise<Listed[]> {
  const response = await page.request.get('/api/sessions');
  return ((await response.json()) as { sessions: Listed[] }).sessions;
}

test.describe('disconnect', () => {
  test.beforeEach(async ({ page }, info) => {
    // A user per test, so sessions from other tests don't count.
    await signIn(
      page,
      `disc-${info.title.replace(/\W+/g, '-').toLowerCase().slice(0, 40)}@example.com`,
    );
  });

  test('before sign-on, Disconnect ends the session', async ({ page }) => {
    await connect(page, 'Fake IBM i');
    expect(await sessions(page)).toHaveLength(1);
    await disconnect(page);
    await expect.poll(() => sessions(page)).toHaveLength(0);
  });

  test('signed on, Leave it running keeps the session for later', async ({ page }) => {
    await connect(page, 'Fake IBM i');
    await signOnFake(page);
    await disconnect(page);
    const dialog = page.getByRole('alertdialog');
    await expect(dialog).toContainText('waits 5 minutes');
    await expect(dialog.getByRole('button')).toHaveText([
      'Stay connected',
      'End session',
      'Leave it running',
    ]);
    await dialog.getByRole('button', { name: 'Leave it running' }).click();
    await expect.poll(async () => (await sessions(page)).map((s) => s.frontends)).toEqual([0]);
  });

  test('signed on, End session ends it on the server', async ({ page }) => {
    await connect(page, 'Fake IBM i');
    await signOnFake(page);
    await disconnect(page);
    await page.getByRole('alertdialog').getByRole('button', { name: 'End session' }).click();
    await expect.poll(() => sessions(page)).toHaveLength(0);
  });

  test('signing off back to the sign-on screen ends the session and closes its tab', async ({
    page,
  }) => {
    await connect(page, 'Fake IBM i');
    await signOnFake(page);
    // F3 on the fake menu signs off: the sign-on screen comes back on the same connection.
    await page.keyboard.press('F3');
    await expect(page.getByRole('tablist', { name: 'Sessions' }).getByRole('tab')).toHaveText([
      'New session',
    ]);
    await expect(page.getByRole('alertdialog')).toHaveCount(0);
    await expect.poll(() => sessions(page)).toHaveLength(0);
  });

  test('signing off says the session ended, and why', async ({ page }) => {
    await connect(page, 'Fake IBM i');
    await signOnFake(page);
    await type(page, '90');
    await page.keyboard.press('Enter');
    const notice = page.getByRole('alertdialog', { name: 'Session ended' });
    await expect(notice).toContainText('usually because you signed off');
    await expect(page.locator('#status-text')).toHaveText('Session ended.');
    await page.keyboard.press('Enter');
    await expect(notice).toHaveCount(0);
    await expect(page.locator('#terminal .offline')).toContainText(
      'usually because you signed off',
    );
  });

  test('a session ended from elsewhere says so', async ({ page }) => {
    await connect(page, 'Fake IBM i');
    const [session] = await sessions(page);
    await page.request.delete(`/api/sessions/${session.id}`, {
      headers: { origin: new URL(page.url()).origin },
    });
    await expect(page.getByRole('alertdialog', { name: 'Ended elsewhere' })).toContainText(
      'ended from another tab or browser',
    );
  });

  test('Disconnect asks for no notice', async ({ page }) => {
    await connect(page, 'Fake IBM i');
    await disconnect(page);
    await expect(page.locator('#status-text')).toHaveText('Disconnected.');
    await expect(page.getByRole('alertdialog')).toHaveCount(0);
  });

  test('Stay connected keeps the session attached', async ({ page }) => {
    await connect(page, 'Fake IBM i');
    await signOnFake(page);
    await disconnect(page);
    await page.keyboard.press('Escape');
    await expect(page.getByRole('alertdialog')).toBeHidden();
    expect((await sessions(page)).map((s) => s.frontends)).toEqual([1]);
  });
});
