import { expect, type APIRequestContext } from '@playwright/test';
import { BASE_URL } from './constants.js';

export async function getStyleUrl(request: APIRequestContext, layerId: number): Promise<string> {
  const response = await request.get(`${BASE_URL}/tiles/${layerId}/tile.json`);
  expect(response.status()).toBe(200);
  const tileJson = await response.json();
  expect(typeof tileJson.style).toBe('string');
  const styleUrl = new URL(tileJson.style);
  expect(styleUrl.origin).toBe(new URL(BASE_URL).origin);
  expect(styleUrl.pathname).toMatch(/^\/api\/styles\/[^/]+\.json$/);
  return styleUrl.href;
}
