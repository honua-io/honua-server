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

If the container is missing browser dependencies, install them with:
```bash
pwsh tests/Honua.Admin.Playwright/bin/Debug/net10.0/playwright.ps1 install-deps
```
