# Admin UI (Blazor WASM)

This document covers how the Honua Admin UI is hosted, configured, and tested.
See ADR-0010 for architectural decisions.

## Hosting Modes

### Integrated (default)

The Admin UI is served by Honua.Server at `/admin`.

Enable/disable:
```bash
# Default: true
HONUA_SERVE_ADMIN_UI=true
```

Note: AOT publishes of Honua.Server do **not** bundle the Admin UI. For AOT builds,
set `HONUA_SERVE_ADMIN_UI=false` and host the UI separately.

Run the server and open:
```
http://localhost:8080/admin/
```

### Standalone (CDN/S3)

Build and host `Honua.Admin` as static assets, then point it to the server API:

1. Update `src/Honua.Admin/wwwroot/appsettings.json`:
```json
{
  "AdminApi": {
    "BaseUrl": "https://api.example.com/api/v1/admin/",
    "Scopes": [ "honua.admin" ]
  }
}
```

2. Build and upload `src/Honua.Admin/bin/Release/net10.0/publish/wwwroot`.

3. Disable integrated hosting on the server and allow the UI origin:
```bash
HONUA_SERVE_ADMIN_UI=false
HONUA_ADMIN_UI_CORS_ORIGINS=https://admin.example.com
```

## Authentication

The Admin UI uses OIDC with PKCE. Configure the identity provider settings in
`src/Honua.Admin/wwwroot/appsettings.json` before deploying.

```json
{
  "Oidc": {
    "Authority": "https://identity.example.com/",
    "ClientId": "honua-admin",
    "ResponseType": "code",
    "DefaultScopes": [ "openid", "profile", "email", "honua.admin" ]
  }
}
```

## Local Development

```bash
dotnet run --project src/Honua.Server
```

Then open `http://localhost:8080/admin/` (integrated) or run the Admin UI
standalone via `dotnet run --project src/Honua.Admin`.

## Connections

Use the Connections page to manage secure PostGIS connections. Provide the host,
port, database, and username, then choose one credential mode:

- **Managed (encrypted)**: supply a password; it is encrypted and stored server-side.
- **External secret**: supply a secret reference (for example
  `aws:secretsmanager:prod-db`) and a secret type.

Use **Save & Test** (or the row-level test action) to validate connectivity.
External secret references cannot be edited in place; delete and recreate the
connection to update the reference.

## Esri Service Import Wizard

Use the Import page to ingest ArcGIS Server FeatureServer/MapServer layers into
PostGIS.

1. Paste an ArcGIS service URL and click **Discover** to list available layers.
2. Select one or more layers, adjust the target table names, and choose options
   (overwrite existing tables and auto-publish).
3. Click **Start import** to queue jobs. Progress, warnings, and completion
   status update live in the Import Jobs list. Failed or cancelled jobs can be
   retried and active jobs can be cancelled.

## File Import (GeoJSON and Others)

Use the Import page to upload supported geospatial files directly into Honua.

1. Select a file and provide a target table name.
2. Click **Preview** to inspect detected SRID and sample attributes.
3. Adjust the target SRID if needed.
4. Click **Import file** to upload; the result reports the created table name
   and feature count.

## Map Preview (MapLibre)

The **Preview** page (`/admin/preview`) embeds MapLibre GL JS for validating
published layers. Select a connection and layer to load vector tiles, then
pan/zoom and click features to view attribute popups.

MapLibre assets live under `src/Honua.Admin/wwwroot/lib/maplibre-gl/` and the
interop helpers live in `src/Honua.Admin/wwwroot/js/maplibre-interop.js`.

## Style Editor (Maputnik)

The **Styles** page (`/admin/styles`) embeds Maputnik for visual MapLibre style
editing. Select a layer, edit its style in Maputnik, and save to persist it via
the admin style API. The right-hand preview updates live against Honua tile
data.

Maputnik assets are bundled under `src/Honua.Admin/wwwroot/maputnik/`. If you
need to refresh the embedded editor, download the latest `dist.zip` from
Maputnik releases and replace the contents of that directory.

## Tests

### bUnit
```bash
dotnet test tests/Honua.Admin.Tests/Honua.Admin.Tests.csproj
```

### Playwright (E2E scaffold)
```bash
dotnet build tests/Honua.Admin.Playwright/Honua.Admin.Playwright.csproj
./tests/Honua.Admin.Playwright/bin/Debug/net10.0/playwright.sh install

HONUA_ADMIN_E2E_BASE_URL=http://localhost:8080/admin/ \
  dotnet test tests/Honua.Admin.Playwright/Honua.Admin.Playwright.csproj
```

Playwright failures write artifacts to `tests/TestResults/playwright/` (trace + screenshot).

Optional DB isolation for E2E runs:
```bash
# Server must be started with HONUA_TEST_SCHEMA_HEADERS=true
HONUA_ADMIN_E2E_DB_URL="Host=localhost;Username=honua;Password=honua;Database=honua_test" \
  HONUA_ADMIN_E2E_BASE_URL=http://localhost:8080/admin/ \
  dotnet test tests/Honua.Admin.Playwright/Honua.Admin.Playwright.csproj
```
When `HONUA_ADMIN_E2E_DB_URL` is set, each test run creates a fresh schema and sends
`X-Honua-Test-Schema` on admin API calls.

If the container is missing browser dependencies, install them with:
```bash
pwsh tests/Honua.Admin.Playwright/bin/Debug/net10.0/playwright.ps1 install-deps
```
