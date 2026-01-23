# Honua Roadmap

This roadmap assumes the MVP described in `docs/MVP_PLAN.md`. The current repo already ships the core server APIs (FeatureServer, OGC API Features/Tiles, OData v4, file import, admin APIs), but MVP items like admin UI, TileJSON, service enable/disable, styles, and deployment templates are still pending. Track open MVP issues (#20, #25, #26, #27, #30, #31, #32, #33, #34, #38, #39, #42, #43, #58, #187, #244).

## Beta (stabilize core + top asks)
- **Query caching:** Short-lived result caching (10-30s) with ETag validation for read-heavy workloads.
- **GeometryServer basics:** project, buffer, simplify.
- **Map output:** MapServer export (PNG/JPEG) for simple maps; limited styling.
- **OData v4 enhancements:** `$expand` navigation, `$apply` aggregation.
- **OGC API Styles:** REST API for style management (list, get, create, update, delete styles).
- **Auth enhancements:** Token revocation with Redis; expand Redis usage for caching/locking.
- **Observability basics:** OpenTelemetry metrics/traces/logs via Aspire dashboard, exception counters, request latency histograms, basic alert rules.
- **Admin UX:** User management, advanced import options, layer-level permissions UI.
- **Extra DBs:** SQL Server (first), MySQL (second).
- **Packaging:** Additional Terraform examples (EKS, AKS, GKE managed K8s clusters).

## GA (breadth + robustness)
- **OData batch:** `/$batch` endpoint for multi-operation requests.
- **Performance & scale:** Connection pooling tuning, cache controls; edge rate limiting templates (nginx/ALB/WAF).
- **Observability:** Full OTel (traces/metrics/logs), dashboards, SLOs; structured audit logging + compliance storage.
- **Protocol depth:** MapServer identify/legend/dynamicLayers; GeometryServer union/intersect/difference; OGC API Maps preview.
- **Outputs:** KML, Shapefile export for small jobs; MVT refinements.
- **Security:** Layer-level RBAC, key rotation, hardened defaults; secure-connection allowlist + audit trail.
- **Cloud storage:** S3/Blob for attachments/exports; lifecycle policies.
- **Additional DBs:** SQLite/DuckDB for single-node/lab use.
- **Tooling:** CLI for admin tasks and migrations.

## Later (enterprise & AI)
- **Legacy OGC:** WFS/WMS/WMTS/WCS/CSW for back-compat.
- **Advanced protocols:** OGC API Tiles/Records/Processes/Coverages/EDR/Routes; STAC; SensorThings.
- **Enterprise data:** Warehouses (Snowflake/BigQuery/Redshift), NoSQL (Cosmos/Mongo), Oracle.
- **AI:** NL → CQL, AI map generation, DevSecOps agent.
- **Workflow:** GeoETL/GeoEvents, scheduler, alerting, dashboard designer.
- **Platform extras:** Gateway, SaaS host, mobile app, intake workers, Map SDK, plugin architecture.
