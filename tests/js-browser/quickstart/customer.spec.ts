import { expect, test } from '@playwright/test';
import { readFileSync } from 'node:fs';

test('archived installation signs in to Console and embedded Studio', async ({ page, context }) => {
  const passwordFile = process.env.HONUA_SHIPPING_PASSWORD_FILE;
  if (!passwordFile) throw new Error('Installation password file is required');
  const password = readFileSync(passwordFile, 'utf8').trim();
  if (!password) throw new Error('Installation password file is empty');
  const errors: string[] = [];
  page.on('pageerror', () => errors.push('browser error'));
  await page.goto('/console/auth/signin');
  await page.locator('input[type="password"]').fill(password);
  const signedIn = page.waitForResponse(response =>
    new URL(response.url()).pathname === '/api/v1/admin/auth/session' && response.request().method() === 'POST');
  await page.getByRole('button', { name: 'Continue', exact: true }).click();
  expect((await signedIn).status()).toBe(204);
  await expect(page.locator('input[type="password"]')).toHaveCount(0);
  const cookie = (await context.cookies()).find(value => value.name === 'honua_admin_session');
  expect(Boolean(cookie?.httpOnly && cookie.secure && cookie.sameSite === 'Strict')).toBe(true);
  const liveRead = page.waitForResponse(response =>
    new URL(response.url()).pathname.replace(/\/$/, '') === '/api/v1/admin/services' && response.request().method() === 'GET');
  await page.goto('/console/');
  expect((await liveRead).status()).toBe(200);
  await expect(page.getByRole('heading', { name: 'Welcome', exact: true })).toBeVisible();
  await page.goto('/console/studio');
  await expect(page.locator('honua-studio-app')).toBeVisible({ timeout: 60_000 });
  await expect(page.getByRole('textbox', { name: 'New map package key', exact: true })).toBeVisible();
  const session = await page.evaluate(async () => {
    const response = await fetch('/api/v1/studio/session');
    const body = await response.json();
    return { status: response.status, authenticated: body.data?.authenticated };
  });
  expect(session).toEqual({ status: 200, authenticated: true });
  const leaked = await page.evaluate(secret => JSON.stringify({ ...localStorage, ...sessionStorage }).includes(secret), password);
  expect(leaked, 'password must stay out of browser storage').toBe(false);
  expect(errors).toEqual([]);
});
