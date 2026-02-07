# Server Management API (Control Plane)

The Server Management API powers the Honua Admin UI and can be used directly for **headless automation**
(e.g., provisioning connections, publishing services, importing data, and orchestration). This is separate from the geospatial data access APIs.

## When to use it
- Integrate Honua into another platform
- Automate publishing workflows
- Run without the Admin UI

## Key entry points
- Admin API root: `/api/v1/admin`
- OpenAPI: `/openapi.json`

## Authentication
Control plane endpoints are typically protected with OIDC or admin credentials.
See `../devops/SECURITY_CONFIGURATION.md` for configuration details.

## Related docs
- `../devops/ADMIN_UI.md` (Admin UI hosting and configuration)
- `API_EXAMPLES.md` (standards API examples)
