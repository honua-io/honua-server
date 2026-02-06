# Honua Server Documentation

This directory contains comprehensive documentation for the Honua Server geospatial platform.

## 📋 Quick Start

New to Honua Server? Start here:
- **[Getting Started Guide](development/getting-started.md)** - Set up your development environment
- **[API Examples](API_EXAMPLES.md)** - Sample API requests and responses
- **[Architecture Overview](ARCHITECTURE.md)** - High-level system design

## 📚 Documentation Categories

### 👥 User Documentation
- **[API Examples](API_EXAMPLES.md)** - Sample requests for all protocols
- **[Model Optimization Guide](MODEL_OPTIMIZATION_GUIDE.md)** - Data model best practices
- **[Coverage Index](specifications/protocol-coverage.md)** - Entry point for protocol coverage docs
- **[OGC API Features Coverage](specifications/ogc-api-features-coverage.md)** - Operations and query parameter support
- **[OData v4 Coverage](specifications/odata-v4-coverage.md)** - Operations and OData query option support
- **[GeoServices FeatureServer Coverage](feature-server-matrix.md)** - FeatureServer operations and parameter support

### 🧑‍💻 Developer Documentation
Architecture Documentation:
- **[Architecture Decision Records (ADRs)](adr/)** - Key architectural decisions and rationale
- **[Architecture Overview](ARCHITECTURE.md)** - System design and component interaction
- **[MVP Plan](MVP_PLAN.md)** - Development phases and roadmap

Developer Resources:
- **[Getting Started](development/getting-started.md)** - Development environment setup
- **[Contributing Guide](development/contributing.md)** - How to contribute to the project
- **[Admin UI Setup](ADMIN_UI.md)** - Hosting and configuration for the Blazor admin app
- **[Pre-PR Checklist](PRE_PR_CHECKLIST.md)** - Code review preparation

Testing and Quality:
- **[Testing Excellence Guide](TESTING_EXCELLENCE_GUIDE.md)** - Testing strategy and best practices
- **[Test Kit Documentation](../tests/Honua.TestKit/README.md)** - Shared testing infrastructure
- **[OData Test Parity](ODATA_TEST_PARITY.md)** - Protocol testing coverage
- **[CITE Conformance Testing](cite-conformance-testing.md)** - Standards compliance testing

### 🛠️ DevOps Documentation
Deployments and Operations:
- **[Infrastructure Deployments](../infrastructure/README.md)** - Docker, Helm, and Terraform options
- **[Serverless Deployments](serverless-deployments.md)** - AWS Lambda + Azure Functions templates
- **[Docker Compose Sample](../infrastructure/docker-compose/README.md)** - Local Docker Compose stack (PostGIS + Redis + Honua Server)
- **[Container Images](CONTAINER_IMAGES.md)** - Publishing, tags, and registry usage
- **[Operational Excellence](OPERATIONAL_EXCELLENCE.md)** - Production best practices
- **[Runbooks](runbooks/README.md)** - Operational playbooks and response steps

Performance and Reliability:
- **[Performance Monitoring](performance-monitoring.md)** - Monitoring setup and metrics
- **[Performance Testing](performance-testing.md)** - Load testing and benchmarking
- **[Caching Strategy](CACHING_STRATEGY.md)** - Redis and in-memory caching
- **[Memory Optimizations](MEMORY_OPTIMIZATIONS_REPORT.md)** - Memory usage optimization
- **[Query Optimization](query-optimization.md)** - Database performance tuning
- **[Connection Pool Sizing](connection-pool-sizing.md)** - Database connection optimization

Security:
- **[Security Configuration](SECURITY_CONFIGURATION.md)** - Secrets, OIDC hardening, and proxy settings
- **[Container Security](CONTAINER_SECURITY.md)** - Docker security best practices
- **[CSP Enhancement](CSP_ENHANCEMENT.md)** - Content Security Policy configuration
- **[Authorization Matrix](AUTHORIZATION_MATRIX.md)** - Endpoint access requirements
- **[Credential Rotation](credential-rotation.md)** - Rotation procedures for secrets and keys
- **[Backup and Restore](backup-restore.md)** - Recovery procedures and RTO/RPO targets
- **[Zero-Downtime Migrations](zero-downtime-migrations.md)** - Safe schema evolution strategy

Troubleshooting:
- **[General Troubleshooting](TROUBLESHOOTING.md)** - Common operational issues
- **[Database Connection Issues](troubleshooting/database-connection-issues.md)** - PostgreSQL and PostGIS problems
- **[Performance Troubleshooting](troubleshooting/performance-troubleshooting.md)** - Query optimization and monitoring
- **[Authentication Problems](troubleshooting/authentication-problems.md)** - API keys and OIDC configuration
- **[Import Process Issues](troubleshooting/import-process-issues.md)** - File format and validation problems
- **[Spatial Query Problems](troubleshooting/spatial-query-problems.md)** - CRS issues and geometry validation

## 🎯 By Role

### For Users
- [API Examples](API_EXAMPLES.md)
- [Model Optimization Guide](MODEL_OPTIMIZATION_GUIDE.md)
- [Protocol Coverage Index](specifications/protocol-coverage.md)

### For Developers
- [Getting Started Guide](development/getting-started.md)
- [Architecture Overview](ARCHITECTURE.md)
- [Contributing Guide](development/contributing.md)
- [Testing Excellence Guide](TESTING_EXCELLENCE_GUIDE.md)

### For DevOps / Operations
- [Infrastructure Deployments](../infrastructure/README.md)
- [Performance Monitoring](performance-monitoring.md)
- [Operational Excellence](OPERATIONAL_EXCELLENCE.md)
- [General Troubleshooting](TROUBLESHOOTING.md)

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
set env vars before startup. See `docs/SECURITY_CONFIGURATION.md` for step-by-step
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

Documentation improvements are welcome! See the [Contributing Guide](development/contributing.md) for:
- Documentation style guidelines
- How to propose new guides
- Template for new troubleshooting scenarios
- Review process for documentation changes

## 💬 Getting Help

**For Development Questions:**
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
