# JavaScript Browser Compatibility Tests

This directory contains the Playwright-based browser compatibility suite used by the JavaScript certification lane.

Canonical contributor documentation: [docs/contributor/testing-javascript.md](../../docs/contributor/testing-javascript.md).

Quick start:

```bash
cd tests/js-browser
npm ci
npx playwright install --with-deps chromium
npm test
```
