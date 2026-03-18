# Honua Server Documentation

Full hosted documentation: **[honua.gitbook.io/honuaio](https://honua.gitbook.io/honuaio/)**

## Start here

| I want to... | Go to |
|---|---|
| **Consume geospatial data** | [Protocols Overview](user/STANDARDS_APIS.md) / [API Examples](user/API_EXAMPLES.md) |
| **Manage the server** | [Admin API](user/CONTROL_PLANE_API.md) / [Admin UI](user/admin-ui.md) |
| **Check server/SDK compatibility** | [Server + SDK Compatibility Matrix](user/SDK_COMPATIBILITY_MATRIX.md) / [Control Plane Migration Guide](user/CONTROL_PLANE_MIGRATION_GUIDE.md) |
| **Standardize SDK release docs** | [SDK Migration Guide Baseline](user/SDK_MIGRATION_GUIDE_BASELINE.md) |
| **Integrate AI agents** | [MCP Server](user/MCP_SERVER.md) |
| **Deploy to production** | [Infrastructure & Deployment](devops/infrastructure.md) |
| **Review enterprise procurement readiness** | [Enterprise Procurement Readiness](user/ENTERPRISE_PROCUREMENT_READINESS.md) |
| **Monitor and troubleshoot** | [Monitoring](devops/monitoring.md) / [Troubleshooting](devops/troubleshooting.md) |
| **Evaluate protocol coverage** | [Coverage Matrices](#coverage-matrices) |
| **Check MVP launch limits** | [MVP Compatibility Contract](user/MVP_COMPATIBILITY_CONTRACT.md) |
| **Contribute** | [Getting Started](contributor/development/getting-started.md) |

## User Documentation

- [User Journeys](user/USER_JOURNEYS.md) — role-based guides
- [Protocols Overview](user/STANDARDS_APIS.md) — FeatureServer, MapServer, OGC, OData, MVT
- [API Examples](user/API_EXAMPLES.md) — request/response examples
- [Integration Patterns](user/INTEGRATION_PATTERNS.md) — common integration approaches
- [Admin API](user/CONTROL_PLANE_API.md) — server management endpoints
- [FileGDB Import Workflow](user/FILEGDB_IMPORT_WORKFLOW.md) — Esri File Geodatabase packaging, preview, upload, and limitations
- [Server + SDK Compatibility Matrix](user/SDK_COMPATIBILITY_MATRIX.md) — supported control-plane server/SDK combinations and migration baseline
- [SDK Migration Guide Baseline](user/SDK_MIGRATION_GUIDE_BASELINE.md) — required migration/changelog structure for SDK repos
- [Control Plane Migration Guide](user/CONTROL_PLANE_MIGRATION_GUIDE.md) — SDK and upgrade workflow
- [Control Plane Versioning Policy](user/CONTROL_PLANE_VERSIONING_POLICY.md) — deprecation and compatibility guarantees
- [MCP Server](user/MCP_SERVER.md) — AI/agent integration via Model Context Protocol
- [Enterprise Procurement Readiness](user/ENTERPRISE_PROCUREMENT_READINESS.md) — buyer-facing security, support, licensing, and architecture package
- [MVP Compatibility Contract](user/MVP_COMPATIBILITY_CONTRACT.md) — launch-ready protocol and limitation summary
- [Cross-Client Certification Matrix](user/CROSS_CLIENT_CERTIFICATION_MATRIX.md) — shared certification vocabulary and common-core test cases for client interoperability
- [Cross-Client Certification Evidence](user/CROSS_CLIENT_CERTIFICATION_EVIDENCE.md) — standardized `.cert.json` evidence format for certification runs
- [Admin UI](user/admin-ui.md) — browser interface guide
- [Data Modeling Guide](user/DATA_MODELING_GUIDE.md) — spatial data modeling

### Coverage Matrices

- [FeatureServer Coverage](user/feature-server-matrix.md)
- [MapServer Coverage](user/map-server-matrix.md) (includes WMS 1.3 and WMTS 1.0)
- [OGC API Features Coverage](user/specifications/ogc-api-features-coverage.md)
- [OGC API Tiles Coverage](user/specifications/ogc-api-tiles-coverage.md)
- [OData v4 Coverage](user/specifications/odata-v4-coverage.md)
- [Geometry Service Coverage](user/specifications/geometry-service-coverage.md)

## DevOps Documentation

- [Infrastructure & Deployment](devops/infrastructure.md) — Docker Compose, Helm, and cloud deployment guidance
- [Deployment Scenarios](devops/DEPLOYMENT_SCENARIOS.md) — patterns by team size
- [Security](devops/security.md) — authentication, authorization, rate limiting, CSP
- [Monitoring & Alerting](devops/monitoring.md) — endpoints, metrics, tracing, cloud alerting
- [Operations](devops/operations.md) — backups, migrations, pools, query tuning, caching
- [Troubleshooting](devops/troubleshooting.md) — database, performance, auth, import, spatial
- [Runbooks](devops/runbooks/README.md) — incident response playbooks

## Contributor Documentation

- [Getting Started](contributor/development/getting-started.md) — development setup
- [Contributing](contributor/development/contributing.md) — code style, architecture rules, PR process
- [Architecture](contributor/ARCHITECTURE.md) — system design
- [Esri Migration Platform Plan](contributor/ESRI_MIGRATION_PLATFORM_PLAN.md) — JS-first migration architecture and phased SDK strategy
- [ADRs](contributor/adr/README.md) — architectural decisions
- [Release Checklist](contributor/RELEASE_CHECKLIST.md) — required compatibility/certification updates per release
