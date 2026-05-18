# Honua QGIS Plugin (staging)

This directory stages the source for the **Honua QGIS plugin** until the
dedicated `honua-qgis` repository is bootstrapped under the `honua-io`
GitHub org. Once that repo exists, the entire `clients/qgis/` tree is
intended to move there verbatim — no honua-server code references it, so
the move is a clean `git mv`.

The plugin lets a QGIS analyst connect to a Honua server with a URL plus
an API key and browse OGC API Features collections and WMS layers from a
dock panel; double-clicking a layer adds it to the project canvas.

## First-slice scope

For issue #808, the first shipped slice covers the two paths that reuse
QGIS's built-in providers end-to-end:

| Layer type | Discovery endpoint | QGIS provider |
| --- | --- | --- |
| Vector | `GET /ogc/features/collections` | `WFS` provider in OGC API Features mode |
| Raster | `GET /ogc/services/{serviceId}/wms?SERVICE=WMS&REQUEST=GetCapabilities` | `wms` provider |

OGC API Tiles, WMTS, FeatureServer transactional, OIDC, and styled-layer
import are deferred to follow-on slices listed at the bottom of this
file.

## Layout

```
clients/qgis/
  honua_qgis/
    metadata.txt            # QGIS Plugin Manager registry fields
    __init__.py             # classFactory(iface)
    plugin.py               # HonuaPlugin -- toolbar + menu wiring
    dialog_add_server.py    # QDialog -- URL + API key + Test connection
    layer_browser.py        # QDockWidget -- vector/raster tree
    client.py               # stdlib-only HTTP + parser layer
    auth.py                 # HonuaConnection model, no PyQt deps
    layers.py               # QGIS provider URI builders (pure strings)
    resources/icon.svg
    i18n/honua_en.ts
  scripts/build_zip.py      # `make zip` worker
  tests/
    conftest.py
    test_auth.py
    test_client.py
    test_layers.py
    test_add_server.py
    test_layer_browser.py
    test_e2e.py             # docker-compose smoke
  docker/docker-compose.yml
  Makefile
  README.md  (this file)
```

## Local workflow

```bash
cd clients/qgis
make zip       # writes dist/honua_qgis.zip
make test      # runs unit tests (no QGIS required)
make lint      # ruff (optional, requires ruff installed)
make e2e       # docker-compose smoke (requires Docker + ghcr image access)
```

### Installing the local zip into QGIS

1. `make zip`
2. In QGIS: *Plugins → Manage and Install Plugins → Install from ZIP*
3. Pick `dist/honua_qgis.zip`
4. Enable the *Honua* plugin
5. Use *Web → Honua → Add Honua Server…* and *Web → Honua → Show Layer
   Browser*

### Running unit tests

The unit tests have no PyQGIS dependency; they exercise the pure Python
helpers (HTTP client, URI builders, validators, view-model). They run
on any Python ≥ 3.8 with `pytest` installed.

### Running the end-to-end test

```bash
make zip
make e2e
```

This:

1. Pulls / starts a `ghcr.io/honua-io/honua-server:ci` container with
   `HONUA_API_KEY=testkey`.
2. Starts a `qgis/qgis:ltr` container, installs the freshly built zip
   into the in-container QGIS profile, and runs `tests/test_e2e.py`.
3. Asserts the OGC API Features ping succeeds and that the first
   discovered collection becomes a valid `QgsVectorLayer`.

Override the server image with `HONUA_IMAGE=…` if you need a specific
build.

## QGIS plugin registry submission

The first published version is `0.1.0` (see `metadata.txt`). The
`experimental=True` flag is on so testers can opt in via *Settings →
Show experimental plugins*; flip to `False` for a stable promotion.

To submit:

1. `make zip`
2. Log in to <https://plugins.qgis.org/> with the `honua-io` org account
   (provision it before submission if it does not already exist).
3. Use the *Upload a plugin* form, attach `dist/honua_qgis.zip`, paste
   the `metadata.txt` `changelog=` line into the release notes field,
   and submit for moderation.
4. Track the moderation ticket; address review comments by bumping the
   version in `metadata.txt`, re-running `make zip`, and re-uploading.

In parallel, attach the same zip to the corresponding GitHub release on
`honua-server` (and later `honua-qgis`) so testers without registry
access can install it manually.

### Continuous distribution

The `qgis-plugin-build` workflow (`.github/workflows/qgis-plugin-build.yml`)
runs on every PR and `trunk` push that touches `clients/qgis/**`,
executes the unit tests, packages the plugin, and uploads
`honua_qgis-zip` as a workflow artifact. Testers can grab the latest
build from the workflow run page without waiting on plugins.qgis.org
moderation. This also gives testers a direct GitHub-hosted zip while the
registry submission is under review.

## Architecture notes

- **No external Python deps.** `client.py` uses `urllib.request` only.
  This is a hard constraint: QGIS bundles its own Python and pip-
  installed packages are fragile across platforms.
- **PyQt5, not PyQt6.** Target QGIS 3.22 LTR (Python 3.9+). Qt-touching
  modules guard their imports so unit tests run on a vanilla
  interpreter.
- **CRS resolution.** `client._extract_storage_crs` parses the OGC
  `storageCrs` URN form (`http://www.opengis.net/def/crs/EPSG/0/4326`)
  and falls back to `EPSG:4326`. WMS layer CRS is taken from the
  capabilities `<CRS>` element.
- **API key transport.** Sent as `X-API-Key` for every plugin HTTP call.
  The provider URIs additionally embed `?apikey=…` because QGIS's
  built-in WFS/WMS providers do not honour custom application-level
  request headers; the Honua server accepts both forms.

## Follow-on tickets (open in honua-qgis once bootstrapped)

| ID | Title |
| --- | --- |
| qgis-2 | OGC API Tiles / WMTS data provider |
| qgis-3 | FeatureServer transactional provider (WFS-T edits) |
| qgis-4 | OIDC / OAuth2 auth flow + `QgsAuthManager` integration |
| qgis-5 | Layer style import (MapLibre Style → QGIS QML) |
