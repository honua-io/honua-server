// Cesium 3D Tiles compatibility against Honua hosted scenes (server #837).
// CERT mapping (protocol=3d-tiles):
//   CERT-CONN-01 — tileset.json reachable, JSON content-type, CORS allowed.
//   CERT-DISC-01 — nested tile asset reachable with correct binary/json
//                  content-type and matching CORS allow-origin.
//   CERT-RNDR-01 — Cesium3DTileset.fromUrl loads, fetches at least one binary
//                  tile body, no Honua 4xx/5xx, no Cesium tileFailed events.
//                  (Pixel-count signal omitted: the committed fixture is a
//                  minimal b3dm with no glTF body, so visible pixels are not
//                  a reliable signal — tile-fetch + tileFailed cover the
//                  "scene rendered without errors" intent of the AC.)
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
const IS_CI = !!process.env.CI;
// Matches nested 3D Tiles binary payload extensions (the formats Honua serves
// from /scenes/{sceneId}/{*assetPath}). Used to verify the smoke actually
// fetched a tile body, not just tileset.json.
const TILE_PAYLOAD_REGEX = /\.(b3dm|i3dm|pnts|cmpt|glb)(\?|$)/i;

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

// Local ad-hoc runs may target a server without a SceneDataset bound to the
// fixture; emit a helpful skip in that case. CI must fail loudly because the
// merge-blocking gate's promise is that a missing route or fixture binding
// surfaces as a hard regression, not a silent skip.
function skipIfLocalAndUnbound(probeStatus: number, hint: string): void {
  if (IS_CI) return;
  test.skip(probeStatus === 404, hint);
}

test.describe('Cesium 3D Tiles scene', () => {
  test('[CERT-CONN-01] tileset.json loads with JSON content-type and CORS', async ({ request }) => {
    const probe = await request.get(TILESET_URL);
    skipIfLocalAndUnbound(
      probe.status(),
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
    skipIfLocalAndUnbound(probe.status(), `Scene fixture missing at /scenes/${SCENE_ID}/tileset.json.`);

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

    // Mirror the tileset.json CORS assertion: a defined-but-mismatched
    // Access-Control-Allow-Origin would still fail in a real browser even
    // though the smoke would otherwise pass. Require either the requesting
    // origin echoed back or the wildcard.
    const acao = assetResponse.headers()['access-control-allow-origin'];
    expect(
      acao,
      `Honua did not return Access-Control-Allow-Origin for nested asset ${resolved}.`,
    ).toBeDefined();
    expect(
      [CORS_TEST_ORIGIN, '*'],
      `Access-Control-Allow-Origin=${acao} does not match Origin=${CORS_TEST_ORIGIN} for nested asset.`,
    ).toContain(acao);
  });

  test('[CERT-RNDR-01] CesiumJS Cesium3DTileset.fromUrl loads nested tile content without failures', async ({ page, request }) => {
    const probe = await request.get(TILESET_URL);
    skipIfLocalAndUnbound(probe.status(), `Scene fixture missing at /scenes/${SCENE_ID}/tileset.json.`);

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
          // Hide the globe entirely so canvas-level signals reflect only the
          // tileset primitive — globe edge antialiasing on a flat-black globe
          // can otherwise paint enough non-background pixels to satisfy a
          // pixel-count check even when no tile content was loaded.
          v.scene.globe.show = false;

          // Capture Cesium-side tile load / parse failures. tileFailed fires
          // when a tile payload 4xx/5xx's, errors out at the network layer,
          // or fails to parse — all of which the smoke must surface as a
          // gate failure rather than passing silently.
          const tileFailures: Array<{ url: string; message: string }> = [];

          try {
            const tileset = await Cesium.Cesium3DTileset.fromUrl(
              `${proxyOrigin}/scenes/${sceneId}/tileset.json`,
            );
            tileset.tileFailed.addEventListener((event: { url: string; message?: unknown }) => {
              tileFailures.push({
                url: event?.url ?? '',
                message: event?.message == null ? '' : String(event.message),
              });
            });
            v.scene.primitives.add(tileset);
            await v.zoomTo(tileset);
            (window as any).__cesiumViewer = v;
            (window as any).__cesiumTileset = tileset;
            (window as any).__cesiumTileFailures = tileFailures;
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

      // Force at least one render so preserveDrawingBuffer captures content
      // and any deferred tile fetches/parses run before the assertions.
      await page.evaluate(() => {
        const v = (window as any).__cesiumViewer;
        if (v) v.scene.requestRender();
      });

      // Network-level failures (DNS, connection refused, etc.) — the proxy
      // pushes these into observer.failures.
      expect(observer.failures, 'Network errors fetching scene assets').toEqual([]);

      // Reject any 4xx/5xx response observed on /scenes/** — Honua serving a
      // 404/5xx for a nested tile body is exactly the kind of regression the
      // smoke must catch and was not surfaced by the previous 2xx-count
      // check (which was satisfied by tileset.json alone).
      const httpFailures = observer.requests.filter((r) => r.status >= 400);
      expect(
        httpFailures,
        'Honua returned 4xx/5xx for one or more nested scene assets',
      ).toEqual([]);

      // Prove a binary tile body actually flowed across the wire — tileset.json
      // alone is not enough evidence that the nested-asset route works. The
      // committed fixture references tiles/0.b3dm at the root, so at least one
      // .b3dm/.glb-shaped request must reach the proxy with 2xx.
      const tilePayloadRequests = observer.requests.filter(
        (r) => TILE_PAYLOAD_REGEX.test(r.url) && r.status >= 200 && r.status < 300,
      );
      expect(
        tilePayloadRequests.length,
        'Cesium3DTileset did not fetch any nested binary tile payload — only tileset.json was loaded.',
      ).toBeGreaterThanOrEqual(1);

      // Cesium-side failures (parse errors, runtime tile-load issues) caught
      // by the tileFailed event registered above.
      const tileFailures = await page.evaluate(
        () => (window as any).__cesiumTileFailures ?? [],
      ) as Array<{ url: string; message: string }>;
      expect(
        tileFailures,
        `Cesium3DTileset reported tileFailed events: ${JSON.stringify(tileFailures)}`,
      ).toEqual([]);
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
