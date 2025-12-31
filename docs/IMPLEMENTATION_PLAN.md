# Implementation Plan

This plan orders the remaining open tickets by dependency and risk. It is intentionally rough and should be adjusted as priorities shift.

## Phase 1: Protocol completeness + conformance
- #197 OGC API Tiles endpoints (vector tiles)
- #198 OGC API Tiles CITE conformance suite
- #199 OGC API Features CITE full pass (remove skips)
- #20 TileJSON metadata endpoint (MVT clients)

## Phase 2: Admin metadata + import versioning (MVP)
- #192 Epic: Admin Metadata API v1
- #191 Admin Metadata API v1 (services/layers/styles)
- #190 Move import endpoints to /api/v1/admin/import
- #189 Docs: Admin Metadata API v1 + import versioning
- #36 Admin endpoint protection
- #58 Service management (enable/disable layers)

## Phase 3: Security, auth, docs (MVP)
- #39 Security hardening and input validation
- #38 Documentation and API docs
- #35 OIDC authentication (Azure AD, Google, generic)

## Phase 4: Deployment & runtime ops
- #31 Helm chart for Kubernetes
- #32 AWS Terraform module
- #33 Azure Terraform module
- #34 GCP Terraform module
- #196 Environment-first configuration
- #194 OpenTelemetry tracing

## Phase 5: Admin UI & UX
- #25 Blazor WASM admin project setup
- #26 PostGIS connection management UI
- #27 Layer publishing from PostGIS tables
- #43 Map preview with MapLibre
- #42 Health dashboard in admin UI
- #30 Embedded Maputnik style editor
- #59 Admin UI wireframes and UX design
- #60 Admin UI Playwright integration tests
- #188 Backend: Esri Service Import + Progress Notifications
- #187 Esri Service Import Wizard UI

## Phase 6: Compatibility + performance (Beta)
- #200 OData client compatibility (Excel/PowerBI)
- #102 Esri Leaflet client compatibility testing
- #201 Performance regression baselines + CI gating
- #202 Load/soak testing for core APIs
- #98 Geometry Server operations (buffer/simplify/project)
- #44 Error handling audit and consistency
