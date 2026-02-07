# Honua Server Documentation

## Start here

| I want to... | Go to |
|---|---|
| Set up a dev environment | [Getting Started](contributor/development/getting-started.md) |
| Deploy to production | [Infrastructure](../infrastructure/README.md) |
| **Consume geospatial data** | [Standards APIs](user/STANDARDS_APIS.md) / [API Examples](user/API_EXAMPLES.md) |
| **Manage the server** | [Control Plane API](user/CONTROL_PLANE_API.md) |
| Contribute code | [Contributing](contributor/development/contributing.md) |

## User documentation

**Getting Started:**
- [User Journeys](user/USER_JOURNEYS.md) — role-based guides for GIS professionals, developers, analysts
- [Geospatial Data APIs](user/STANDARDS_APIS.md) — FeatureServer, OGC API Features/Tiles, OData v4, MVT
- [API Examples](user/API_EXAMPLES.md) — request/response examples for geospatial data access

**Integration & Architecture:**
- [Integration Patterns](user/INTEGRATION_PATTERNS.md) — common integration approaches and code examples
- [Server Management API](user/CONTROL_PLANE_API.md) — admin and automation endpoints
- [Data Modeling Guide](user/DATA_MODELING_GUIDE.md) — spatial data modeling best practices

**Reference:**
- [FeatureServer Coverage](user/feature-server-matrix.md) — operation and parameter support
- [Protocol Coverage Index](user/specifications/protocol-coverage.md) — standards coverage overview

## DevOps documentation

**Deployment:**
- [Deployment Scenarios](devops/DEPLOYMENT_SCENARIOS.md) — specific deployment patterns for different use cases
- [Infrastructure](../infrastructure/README.md) — Docker Compose, Helm, Terraform
- [Serverless](devops/serverless-deployments.md) — AWS Lambda and Azure Functions
- [Container Images](devops/CONTAINER_IMAGES.md) — registries, tags, and publishing

**Operations:**
- [Operational Excellence](devops/OPERATIONAL_EXCELLENCE.md) — current operational tooling
- [Runbooks](devops/runbooks/README.md) — incident response playbooks
- [Troubleshooting](devops/TROUBLESHOOTING.md) — common issues and fixes
  - [Database connections](devops/troubleshooting/database-connection-issues.md)
  - [Performance](devops/troubleshooting/performance-troubleshooting.md)
  - [Authentication](devops/troubleshooting/authentication-problems.md)
  - [Imports](devops/troubleshooting/import-process-issues.md)
  - [Spatial queries](devops/troubleshooting/spatial-query-problems.md)

**Performance:**
- [Performance Monitoring](devops/performance-monitoring.md)
- [Caching Strategy](devops/CACHING_STRATEGY.md)
- [Query Optimization](devops/query-optimization.md)
- [Connection Pool Sizing](devops/connection-pool-sizing.md)

**Security:**
- [Security Configuration](devops/SECURITY_CONFIGURATION.md) — OIDC, secrets, proxy settings
- [Authorization Matrix](devops/AUTHORIZATION_MATRIX.md) — endpoint access requirements
- [Container Security](devops/CONTAINER_SECURITY.md)
- [Credential Rotation](devops/credential-rotation.md)
- [Backup and Restore](devops/backup-restore.md)

## Contributor documentation

**Development:**
- [Getting Started](contributor/development/getting-started.md) — prerequisites, setup, first run
- [Contributing](contributor/development/contributing.md) — code style, testing, PR process
- [K3d + Helm](contributor/development/k3d-helm.md) — local Kubernetes development

**Architecture:**
- [Architecture Overview](contributor/ARCHITECTURE.md) — system design and component interaction
- [Architecture Diagrams](contributor/ARCHITECTURE_DIAGRAMS.md) — visual system diagrams
- [ADRs](contributor/adr/) — architecture decision records

**Testing:**
- [Testing Guide](contributor/TESTING_EXCELLENCE_GUIDE.md) — strategy and best practices
- [CI Quality Gates](contributor/CI_QUALITY_GATES.md) — CI workflows and enforcement
- [Pre-PR Checklist](contributor/PRE_PR_CHECKLIST.md) — code review preparation
