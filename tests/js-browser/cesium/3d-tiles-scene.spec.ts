// Cesium 3D Tiles compatibility against Honua hosted scenes (server #837).
// CERT mapping (protocol=3d-tiles):
//   CERT-CONN-01 — tileset.json reachable, JSON content-type, CORS allowed.
//   CERT-DISC-01 — nested tile asset reachable with correct binary/json content-type.
//   CERT-RNDR-01 — Cesium3DTileset.fromUrl loads, renders nonblank pixels, no failed tile fetches.
//   CERT-AUTH-01 — protected scene browser handoff deferred to honua-server-849.
//
// The harness routes /scenes/* through the same-origin Node proxy that already
// serves the CesiumJS bundle; 3D Tiles content URIs in tileset.json are
// rewritten to the proxy origin via the existing JSON text replacement so
// nested asset requests are observed by the Playwright network observer
// without any client-side URL manipulation.

import { test, expect } from '@playwright/test';
import {
  createViewer,
  observeImageryRequests,
} from './support/cesium-harness.js';
import { BASE_URL, SCENE_ID, CORS_TEST_ORIGIN } from './support/constants.js';

const TILESET_URL = `${BASE_URL}/scenes/${SCENE_ID}/tileset.json`;
// Single-mesh fixture rendered against a black background — the threshold is
// intentionally minimal so a small render still trips the "nonblank" signal
// without baking in pixel-exact baselines.
const RENDERED_PIXEL_THRESHOLD = 8;

interface TilesetContent { uri?: string }
interface TilesetNode { content?: TilesetContent; children?: TilesetNode[] }

function findFirstContentUri(root: TilesetNode | undefined): string | null {
  if (!root) return null;
  if (root.content?.uri) return root.content.uri;
  for (const child of root.children ?? []) {
    const uri = findFirstContentUri(child);
    if (uri) return uri;
  }
  return null;
}

function expectedContentType(uri: string): RegExp {
  const lower = uri.toLowerCase();
  if (lower.endsWith('.json')) return /application\/json/;
  if (lower.endsWith('.glb')) return /model\/gltf-binary|application\/octet-stream/;
  // .b3dm/.i3dm/.pnts/.cmpt are all binary tile payloads — Honua serves them as
  // application/octet-stream by default. model/gltf-binary is also acceptable
  // for forward-compatible glTF-based payloads.
  return /application\/octet-stream|model\/gltf-binary/;
}

test.describe('Cesium 3D Tiles scene', () => {
  test('[CERT-CONN-01] tileset.json loads with JSON content-type and CORS', async ({ request }) => {
    const probe = await request.get(TILESET_URL);
    test.skip(
      probe.status() === 404,
      `Scene fixture not configured at /scenes/${SCENE_ID}/tileset.json. ` +
      'Bind a SceneDataset entry (Scenes:Datasets:0:Id, AssetRoot) to ' +
      'tests/fixtures/scenes/fixture-tileset on the server under test.',
    );

    expect(probe.status()).toBe(200);
    const contentType = probe.headers()['content-type'] ?? '';
    expect(contentType.toLowerCase()).toContain('application/json');

    const body = await probe.json();
    expect(body).toHaveProperty('asset');
    expect(body.asset).toHaveProperty('version');
    expect(body).toHaveProperty('geometricError');
    expect(body).toHaveProperty('root');

    // CORS must echo the requesting origin (or '*') so CesiumJS in a real
    // browser can fetch tileset.json cross-origin. Probe with an explicit
    // Origin header set to the value the smoke harness expects to be in the
    // server's Cors:AllowedOrigins list. A request with no Origin header
    // would not exercise the CORS path at all.
    const corsResponse = await request.get(TILESET_URL, {
      headers: { Origin: CORS_TEST_ORIGIN },
    });
    expect(corsResponse.status()).toBe(200);
    const acao = corsResponse.headers()['access-control-allow-origin'];
    expect(
      acao,
      `Honua did not return Access-Control-Allow-Origin for Origin=${CORS_TEST_ORIGIN}. ` +
      'Add this origin to Cors:AllowedOrigins on the server under test ' +
      '(see tests/js-browser/cesium/README.md).',
    ).toBeDefined();
    expect([CORS_TEST_ORIGIN, '*']).toContain(acao);
  });

  test('[CERT-DISC-01] nested tile asset loads with correct content-type and CORS', async ({ request }) => {
    const probe = await request.get(TILESET_URL);
    test.skip(probe.status() === 404, `Scene fixture missing at /scenes/${SCENE_ID}/tileset.json.`);

    const body = await probe.json();
    const childUri = findFirstContentUri(body.root);
    expect(
      childUri,
      'Fixture tileset.json must declare at least one tile content URI for CERT-DISC-01.',
    ).not.toBeNull();

    // Resolve the URI relative to the tileset URL — the OGC 3D Tiles spec
    // is explicit that nested content URIs are relative to the document
    // they appear in.
    const resolved = new URL(childUri!, TILESET_URL).toString();

    const assetResponse = await request.get(resolved, {
      headers: { Origin: CORS_TEST_ORIGIN },
    });
    expect(assetResponse.status()).toBe(200);

    const ct = (assetResponse.headers()['content-type'] ?? '').toLowerCase();
    expect(ct).toMatch(expectedContentType(childUri!));

    const acao = assetResponse.headers()['access-control-allow-origin'];
    expect(
      acao,
      `Honua did not return Access-Control-Allow-Origin for nested asset ${resolved}.`,
    ).toBeDefined();
  });

  test('[CERT-RNDR-01] CesiumJS Cesium3DTileset.fromUrl renders a nonblank scene', async ({ page, request }) => {
    const probe = await request.get(TILESET_URL);
    test.skip(probe.status() === 404, `Scene fixture missing at /scenes/${SCENE_ID}/tileset.json.`);

    const observer = await observeImageryRequests(page, '**/scenes/**');

    try {
      const viewer = await createViewer(page);

      const evalResult = await page.evaluate(
        async ({ proxyOrigin, sceneId }) => {
          const Cesium = (window as any).Cesium;
          const v = new Cesium.Viewer('cesiumContainer', {
            baseLayer: false,
            baseLayerPicker: false,
            timeline: false,
            animation: false,
            geocoder: false,
            homeButton: false,
            sceneModePicker: false,
            navigationHelpButton: false,
            fullscreenButton: false,
            infoBox: false,
            selectionIndicator: false,
            contextOptions: { webgl: { preserveDrawingBuffer: true } },
          });
          v.scene.backgroundColor = Cesium.Color.BLACK;
          if (v.scene.skyBox) v.scene.skyBox.show = false;
          if (v.scene.skyAtmosphere) v.scene.skyAtmosphere.show = false;
          if (v.scene.sun) v.scene.sun.show = false;
          if (v.scene.moon) v.scene.moon.show = false;
          v.scene.globe.baseColor = Cesium.Color.BLACK;
          v.scene.globe.showGroundAtmosphere = false;

          try {
            const tileset = await Cesium.Cesium3DTileset.fromUrl(
              `${proxyOrigin}/scenes/${sceneId}/tileset.json`,
            );
            v.scene.primitives.add(tileset);
            await v.zoomTo(tileset);
            (window as any).__cesiumViewer = v;
            (window as any).__cesiumTileset = tileset;
            return { ok: true };
          } catch (err) {
            return { ok: false, error: (err as Error)?.message ?? String(err) };
          }
        },
        { proxyOrigin: viewer.proxyOrigin, sceneId: SCENE_ID },
      );

      expect(evalResult.ok, `Cesium3DTileset.fromUrl failed: ${evalResult.error ?? ''}`).toBe(true);

      // Cesium3DTileset is a scene primitive. The harness'
      // waitForTilesLoaded() helper polls viewer.scene.globe.tilesLoaded,
      // which only reflects imagery-layer state and never fires for a
      // tileset primitive. Poll the tileset's own tilesLoaded flag instead.
      await page.waitForFunction(
        () => (window as any).__cesiumTileset?.tilesLoaded === true,
        { timeout: 30_000 },
      );

      // Force at least one render so preserveDrawingBuffer captures content.
      await page.evaluate(() => {
        const v = (window as any).__cesiumViewer;
        if (v) v.scene.requestRender();
      });

      // 3D tile payloads are model/gltf-binary or application/octet-stream;
      // the harness' successfulImageResponses() filters on `image/` and
      // would always return 0 for tileset traffic.
      const successfulTileRequests = observer.requests.filter(
        (r) => r.status >= 200 && r.status < 300,
      );
      expect(successfulTileRequests.length).toBeGreaterThanOrEqual(1);
      expect(observer.failures).toEqual([]);

      expect(await viewer.countNonBackgroundPixels({ r: 0, g: 0, b: 0 }))
        .toBeGreaterThan(RENDERED_PIXEL_THRESHOLD);
    } finally {
      await observer.dispose();
    }
  });

  // Cesium browser clients cannot attach Authorization headers to nested
  // tile fetches issued by Cesium3DTileset; the planned solution is signed
  // URLs delivered by honua-server-849. Track skip in the cert envelope
  // until that lands.
  test.skip(
    '[CERT-AUTH-01] protected scene returns 401 — DEFERRED to honua-server-849',
    () => {
      // Intentionally empty. Cert reporter records this as `skip`.
    },
  );
});
