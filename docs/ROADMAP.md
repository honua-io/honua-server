# Honua Roadmap

This roadmap assumes the MVP described in `docs/MVP_PLAN.md` (full FeatureServer + OGC API Features + OData v4 with spatial + CRUD + MVT + file import + CRS support on PostGIS + GeoServices Import Wizard + embedded Maputnik style editor + OIDC authentication + Helm/Terraform deployment templates) is delivered; the current repo is in planning/Phase 0.

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
- **Performance & scale:** Connection pooling tuning, cache controls; rate limiting via proxy guidance.
- **Observability:** Full OTel (traces/metrics/logs), dashboards, SLOs; structured audit logging.
- **Protocol depth:** MapServer identify/legend/dynamicLayers; GeometryServer union/intersect/difference; OGC API Maps preview.
- **Outputs:** KML, Shapefile export for small jobs; MVT refinements.
- **Security:** Layer-level RBAC, key rotation, hardened defaults.
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
