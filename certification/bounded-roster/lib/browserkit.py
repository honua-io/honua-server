"""Headless-Chromium driver for the ``js-maplibre`` lane.

The page (``lanes/maplibre/page/index.html``) is served same-origin by the wire
proxy and loads the pinned MapLibre release. Every map is created, styled and
queried through MapLibre's public API (``Map``, sources, ``transformRequest``,
``queryRenderedFeatures``, ``project``, ``queryTerrainElevation`` and the ``error``
event); the browser's own network events record what the client requested.
"""
from __future__ import annotations

import json
from contextlib import contextmanager
from dataclasses import dataclass, field

from playwright.sync_api import sync_playwright

CHROMIUM_ARGS = ["--use-angle=swiftshader", "--enable-unsafe-swiftshader", "--ignore-gpu-blocklist"]

_MAP_SCRIPT = """
async ({style, center, zoom, headers, probes, terrain, timeout}) => {
  const errors = [];
  const map = new maplibregl.Map({
    container: 'map', style, center, zoom, fadeDuration: 0,
    canvasContextAttributes: {preserveDrawingBuffer: true}, preserveDrawingBuffer: true,
    transformRequest: (url) => (headers && url.startsWith(location.origin) && !url.includes('/__roster/'))
      ? {url, headers} : {url},
  });
  map.on('error', (event) => errors.push({
    status: event.error && event.error.status, message: String(event.error && event.error.message),
    url: event.error && event.error.url}));
  if (terrain) { map.on('load', () => map.setTerrain(terrain)); }
  const idle = await Promise.race([
    new Promise((resolve) => map.once('idle', () => resolve(true))),
    new Promise((resolve) => setTimeout(() => resolve(false), timeout)),
  ]);
  if (terrain) {
    await new Promise((resolve) => setTimeout(resolve, 500));
    await Promise.race([new Promise((resolve) => map.once('idle', resolve)), new Promise((r) => setTimeout(r, 5000))]);
  }
  const canvas = map.getCanvas();
  const gl = canvas.getContext('webgl2') || canvas.getContext('webgl');
  const pixelAt = (x, y) => {
    const ratio = window.devicePixelRatio || 1;
    const buffer = new Uint8Array(4);
    gl.readPixels(Math.round(x * ratio), Math.round(canvas.height - y * ratio), 1, 1, gl.RGBA, gl.UNSIGNED_BYTE, buffer);
    return Array.from(buffer);
  };
  const results = {};
  for (const probe of probes || []) {
    const point = map.project(probe.lngLat);
    const entry = {point: [point.x, point.y]};
    if (probe.query) {
      entry.features = map.queryRenderedFeatures([[point.x - 6, point.y - 6], [point.x + 6, point.y + 6]])
        .map((feature) => ({layer: feature.layer.id, sourceLayer: feature.sourceLayer, properties: feature.properties}));
    }
    if (probe.pixel) {
      let painted = 0;
      for (let dx = -4; dx <= 4; dx++) for (let dy = -4; dy <= 4; dy++) {
        const pixel = pixelAt(point.x + dx, point.y + dy);
        if (pixel[3] > 0 && !(pixel[0] > 250 && pixel[1] > 250 && pixel[2] > 250)) painted++;
      }
      entry.painted = painted;
    }
    if (probe.elevation) {
      entry.elevation = map.queryTerrainElevation ? map.queryTerrainElevation(probe.lngLat) : null;
    }
    results[probe.name] = entry;
  }
  const version = maplibregl.getVersion ? maplibregl.getVersion() : maplibregl.version;
  map.remove();
  return {idle, errors, probes: results, version};
}
"""


@dataclass
class Response:
    method: str
    url: str
    status: int
    content_type: str | None


@dataclass
class Browser:
    release: str
    base_url: str
    responses: list[Response] = field(default_factory=list)
    page: object = None

    def render(self, style: dict, *, center, zoom: float, headers: dict | None = None,
               probes: list[dict] | None = None, terrain: dict | None = None, timeout: int = 30000) -> dict:
        self.responses.clear()
        outcome = self.page.evaluate(_MAP_SCRIPT, {
            "style": style, "center": center, "zoom": zoom, "headers": headers, "probes": probes or [],
            "terrain": terrain, "timeout": timeout})
        outcome["responses"] = [response.__dict__ for response in self.responses]
        return outcome

    def fetch(self, url: str, headers: dict | None = None) -> dict:
        """An application-level fetch from the map page (not a MapLibre API call)."""
        return self.page.evaluate("""async ({url, headers}) => {
            const response = await fetch(url, {headers: headers || {}});
            const body = await response.text();
            return {status: response.status, contentType: response.headers.get('content-type'), body};
        }""", {"url": url, "headers": headers})

    def matching(self, fragment: str) -> list[Response]:
        return [response for response in self.responses if fragment in response.url]


@contextmanager
def browser(release: str, base_url: str):
    with sync_playwright() as playwright:
        chromium = playwright.chromium.launch(args=CHROMIUM_ARGS)
        context = chromium.new_context(viewport={"width": 512, "height": 512})
        page = context.new_page()
        session = Browser(release=release, base_url=base_url, page=page)

        def on_response(response) -> None:
            if "/__roster/" in response.url:
                return
            session.responses.append(Response(
                method=response.request.method, url=response.url, status=response.status,
                content_type=response.headers.get("content-type")))

        page.on("response", on_response)
        page.goto(f"{base_url}/__roster/index.html?maplibre={release}")
        page.wait_for_function("window.maplibreReady !== undefined", timeout=60000)
        ready = page.evaluate("window.maplibreReady")
        if ready is not True:
            raise RuntimeError(f"MapLibre {release} failed to load: {ready}")
        try:
            yield session
        finally:
            context.close()
            chromium.close()


def summarize(outcome: dict, limit: int = 6) -> str:
    responses = outcome.get("responses", [])
    statuses = sorted({(response["status"], (response["content_type"] or "").split(";")[0]) for response in responses})
    return json.dumps({"idle": outcome.get("idle"), "errors": outcome.get("errors", [])[:limit],
                       "responses": len(responses), "status_types": statuses[:limit]})
