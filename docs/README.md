# Honua Server Documentation

This directory contains comprehensive documentation for the Honua Server geospatial platform.

## 📋 Quick Start

New to Honua Server? Start here:
- **[User Docs](user/README.md)** - Control plane + standards APIs
- **[DevOps Docs](devops/README.md)** - Deploy, operate, and monitor
- **[Contributor Docs](contributor/README.md)** - Build and extend Honua

## 📚 Documentation Categories

### 👥 User Documentation
- **[Control Plane API (Honua)](user/CONTROL_PLANE_API.md)** - Admin + automation API
- **[Standards APIs](user/STANDARDS_APIS.md)** - FeatureServer, OGC, OData, MVT
- **[API Examples](user/API_EXAMPLES.md)** - Sample requests for standards APIs
- **[Model Optimization Guide](user/MODEL_OPTIMIZATION_GUIDE.md)** - Data model best practices
- **[FeatureServer Coverage](user/feature-server-matrix.md)** - FeatureServer operations and parameters
- **[Protocol Coverage Index](user/specifications/protocol-coverage.md)** - Standards coverage overview

### 🧑‍💻 Contributor Documentation
Architecture Documentation:
- **[Architecture Decision Records (ADRs)](contributor/adr/)** - Key architectural decisions and rationale
- **[Architecture Overview](contributor/ARCHITECTURE.md)** - System design and component interaction
- **[Architecture Diagrams](contributor/ARCHITECTURE_DIAGRAMS.md)** - System diagrams
- **[MVP Plan](contributor/MVP_PLAN.md)** - Development phases and roadmap

Contributor Resources:
- **[Agent Instructions](../AGENTS.md)** - Canonical agent and project rules
- **[Getting Started](contributor/development/getting-started.md)** - Development environment setup
- **[Contributing Guide](contributor/development/contributing.md)** - How to contribute to the project
- **[Pre-PR Checklist](contributor/PRE_PR_CHECKLIST.md)** - Code review preparation

Testing and Quality:
- **[Testing Excellence Guide](contributor/TESTING_EXCELLENCE_GUIDE.md)** - Testing strategy and best practices
- **[Test Kit Documentation](../tests/Honua.TestKit/README.md)** - Shared testing infrastructure
- **[OData Test Parity](contributor/ODATA_TEST_PARITY.md)** - Protocol testing coverage
- **[CITE Conformance Testing](contributor/cite-conformance-testing.md)** - Standards compliance testing

### 🛠️ DevOps Documentation
Deployments and Operations:
- **[Infrastructure Deployments](../infrastructure/README.md)** - Docker, Helm, and Terraform options
- **[Serverless Deployments](devops/serverless-deployments.md)** - AWS Lambda + Azure Functions templates
- **[Docker Compose Sample](../infrastructure/docker-compose/README.md)** - Local Docker Compose stack (PostGIS + Redis + Honua Server)
- **[Container Images](devops/CONTAINER_IMAGES.md)** - Publishing, tags, and registry usage
- **[Operational Excellence](devops/OPERATIONAL_EXCELLENCE.md)** - Production best practices
- **[Runbooks](devops/runbooks/README.md)** - Operational playbooks and response steps

Performance and Reliability:
- **[Performance Monitoring](devops/performance-monitoring.md)** - Monitoring setup and metrics
- **[Performance Testing](devops/performance-testing.md)** - Load testing and benchmarking
- **[Caching Strategy](devops/CACHING_STRATEGY.md)** - Redis and in-memory caching
- **[Memory Optimizations](devops/MEMORY_OPTIMIZATIONS_REPORT.md)** - Memory usage optimization
- **[Query Optimization](devops/query-optimization.md)** - Database performance tuning
- **[Connection Pool Sizing](devops/connection-pool-sizing.md)** - Database connection optimization

Security:
- **[Security Configuration](devops/SECURITY_CONFIGURATION.md)** - Secrets, OIDC hardening, and proxy settings
- **[Container Security](devops/CONTAINER_SECURITY.md)** - Docker security best practices
- **[CSP Enhancement](devops/CSP_ENHANCEMENT.md)** - Content Security Policy configuration
- **[Authorization Matrix](devops/AUTHORIZATION_MATRIX.md)** - Endpoint access requirements
- **[Credential Rotation](devops/credential-rotation.md)** - Rotation procedures for secrets and keys
- **[Backup and Restore](devops/backup-restore.md)** - Recovery procedures and RTO/RPO targets
- **[Zero-Downtime Migrations](devops/zero-downtime-migrations.md)** - Safe schema evolution strategy

Troubleshooting:
- **[General Troubleshooting](devops/TROUBLESHOOTING.md)** - Common operational issues
- **[Database Connection Issues](devops/troubleshooting/database-connection-issues.md)** - PostgreSQL and PostGIS problems
- **[Performance Troubleshooting](devops/troubleshooting/performance-troubleshooting.md)** - Query optimization and monitoring
- **[Authentication Problems](devops/troubleshooting/authentication-problems.md)** - API keys and OIDC configuration
- **[Import Process Issues](devops/troubleshooting/import-process-issues.md)** - File format and validation problems
- **[Spatial Query Problems](devops/troubleshooting/spatial-query-problems.md)** - CRS issues and geometry validation

## 🎯 By Role

### For Users
- [Control Plane API](user/CONTROL_PLANE_API.md)
- [Standards APIs](user/STANDARDS_APIS.md)
- [API Examples](user/API_EXAMPLES.md)

### For Contributors
- [Agent Instructions](../AGENTS.md)
- [Getting Started Guide](contributor/development/getting-started.md)
- [Architecture Overview](contributor/ARCHITECTURE.md)
- [Contributing Guide](contributor/development/contributing.md)
- [Testing Excellence Guide](contributor/TESTING_EXCELLENCE_GUIDE.md)

### For DevOps / Operations
- [Infrastructure Deployments](../infrastructure/README.md)
- [Performance Monitoring](devops/performance-monitoring.md)
- [Operational Excellence](devops/OPERATIONAL_EXCELLENCE.md)
- [General Troubleshooting](devops/TROUBLESHOOTING.md)

## 📋 Quick Reference

### Common Commands
```bash
# Development
docker compose up -d              # Start development environment
docker compose --profile redis up -d  # Optional Redis
docker compose --profile minio up -d  # Optional MinIO
dotnet test                       # Run all tests
dotnet format Honua.sln          # Format code
curl http://localhost:8080/health # Health check

# Troubleshooting
docker logs honua-server          # Application logs
psql -h localhost -U postgres -d honua  # Database access
redis-cli ping                    # Redis connectivity
```

### Environment Variables
```bash
# Database
ConnectionStrings__DefaultConnection="Host=localhost;Database=honua;Username=postgres;Password=postgres"

# Authentication
OIDC__ENABLED=true
OIDC__AZUREAD__ENABLED=true
OIDC__AZUREAD__TENANTID="your-tenant-id"
OIDC__AZUREAD__CLIENTID="your-client-id"
OIDC__TOKENVALIDATION__VALIDAUDIENCES__0="api://your-client-id"
OIDC__ADMINROLES__0="admin"
HONUA_ADMIN_PASSWORD="secure-api-key" # automation only, not for browser UI

# Performance
Limits__Query__MaxRecordCount=2000
Cache__Provider="Redis"
```

### Important URLs
```
Health Check:     http://localhost:8080/health
Admin UI:         http://localhost:8080/admin
Feature Server:   http://localhost:8080/rest/services/1/FeatureServer
OGC Features:     http://localhost:8080/collections
OData Endpoint:   http://localhost:8080/odata
```

### Container Images
```bash
# Docker Hub (default)
docker pull honuaio/honua-server:latest

# GHCR
docker pull ghcr.io/honua-io/honua-server:latest
```

Tags:
- `latest` (trunk)
- `vX.Y.Z`, `vX.Y`, `vX` (release tags)
- `nightly` (nightly JIT)
- `nightly-aot` (nightly AOT)

Note: When OIDC is enabled, the `/odata` endpoints require bearer token auth (Power BI/Excel can use Organizational Account or Web API).

### OIDC Bootstrap (Initial Setup)
Honua does not provide an in-app bootstrap flow for OIDC. Configure your IdP and
set env vars before startup. See `docs/devops/SECURITY_CONFIGURATION.md` for step-by-step
examples and role mapping guidance.

## 📈 Documentation Status

### Comprehensive Coverage
- ✅ Architecture decisions documented (22 ADRs)
- ✅ Development setup and contribution guidelines
- ✅ Troubleshooting guides for common issues
- ✅ Performance optimization strategies
- ✅ Testing strategy and requirements

### Key Documentation Principles
- **Problem-Solution Oriented**: Each guide addresses specific real-world issues
- **Runnable Examples**: All code snippets are tested and functional
- **Progressive Disclosure**: Information organized from basic to advanced
- **Searchable**: Clear headings and consistent structure for easy navigation

## 🔄 Keeping Documentation Current

### Update Triggers
- New architectural decisions → Add ADR
- Common support issues → Update troubleshooting guides
- Performance improvements → Update optimization guides
- API changes → Update examples and guides

### Review Schedule
- **Monthly**: Review troubleshooting guides for new common issues
- **Quarterly**: Update performance benchmarks and optimization guides
- **Per Release**: Update API examples and architectural documentation

## 🤝 Contributing to Documentation

Documentation improvements are welcome! See the [Contributing Guide](contributor/development/contributing.md) for:
- Documentation style guidelines
- How to propose new guides
- Template for new troubleshooting scenarios
- Review process for documentation changes

## 💬 Getting Help

**For Contributor Questions:**
- Check relevant troubleshooting guide first
- Search existing GitHub Issues and Discussions
- Create new GitHub Discussion for questions

**For Documentation Issues:**
- Missing information → Create GitHub Issue
- Incorrect information → Create GitHub Issue with correction
- New scenario needed → Propose in GitHub Discussion

**Emergency Support:**
- Check [Operational Excellence](OPERATIONAL_EXCELLENCE.md) for incident response
- Follow troubleshooting guides for immediate issues
- Escalate through standard support channels

---

*This documentation is living and continuously improved based on real-world usage and feedback.*
