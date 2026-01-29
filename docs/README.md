# Honua Server Documentation

This directory contains comprehensive documentation for the Honua Server geospatial platform.

## 📋 Quick Start

New to Honua Server? Start here:
- **[Getting Started Guide](development/getting-started.md)** - Set up your development environment
- **[API Examples](API_EXAMPLES.md)** - Sample API requests and responses
- **[Architecture Overview](ARCHITECTURE.md)** - High-level system design

## 📚 Documentation Categories

### 🏗️ Architecture Documentation
- **[Architecture Decision Records (ADRs)](adr/)** - Key architectural decisions and rationale
- **[Architecture Overview](ARCHITECTURE.md)** - System design and component interaction
- **[MVP Plan](MVP_PLAN.md)** - Development phases and roadmap

### 🛠️ Developer Resources
- **[Getting Started](development/getting-started.md)** - Development environment setup
- **[Admin UI Setup](ADMIN_UI.md)** - Hosting and configuration for the Blazor admin app
- **[Contributing Guide](development/contributing.md)** - How to contribute to the project
- **[Testing Excellence Guide](TESTING_EXCELLENCE_GUIDE.md)** - Testing strategy and best practices
- **[Pre-PR Checklist](PRE_PR_CHECKLIST.md)** - Code review preparation

### 🚨 Troubleshooting Guides
- **[Database Connection Issues](troubleshooting/database-connection-issues.md)** - PostgreSQL and PostGIS problems
- **[Performance Troubleshooting](troubleshooting/performance-troubleshooting.md)** - Query optimization and monitoring
- **[Authentication Problems](troubleshooting/authentication-problems.md)** - API keys and OIDC configuration
- **[Import Process Issues](troubleshooting/import-process-issues.md)** - File format and validation problems
- **[Spatial Query Problems](troubleshooting/spatial-query-problems.md)** - CRS issues and geometry validation
- **[General Troubleshooting](TROUBLESHOOTING.md)** - Common operational issues

### 🎯 Operational Excellence
- **[Performance Monitoring](performance-monitoring.md)** - Monitoring setup and metrics
- **[Caching Strategy](CACHING_STRATEGY.md)** - Redis and in-memory caching
- **[Container Images](CONTAINER_IMAGES.md)** - Publishing, tags, and registry usage
- **[Backup and Restore](backup-restore.md)** - Recovery procedures and RTO/RPO targets
- **[Zero-Downtime Migrations](zero-downtime-migrations.md)** - Safe schema evolution strategy
- **[Credential Rotation](credential-rotation.md)** - Rotation procedures for secrets and keys
- **[Operational Excellence](OPERATIONAL_EXCELLENCE.md)** - Production best practices
- **[Infrastructure Samples](../infrastructure/samples/README.md)** - Docker Compose and other IaC examples

### 🧪 Testing and Quality
- **[Test Kit Documentation](../tests/Honua.TestKit/README.md)** - Shared testing infrastructure
- **[OData Test Parity](ODATA_TEST_PARITY.md)** - Protocol testing coverage
- **[CITE Conformance Testing](cite-conformance-testing.md)** - Standards compliance testing

### 📊 Performance and Optimization
- **[Performance Testing](performance-testing.md)** - Load testing and benchmarking
- **[Memory Optimizations](MEMORY_OPTIMIZATIONS_REPORT.md)** - Memory usage optimization
- **[Query Optimization](query-optimization.md)** - Database performance tuning
- **[Connection Pool Sizing](connection-pool-sizing.md)** - Database connection optimization

### 🔒 Security
- **[Container Security](CONTAINER_SECURITY.md)** - Docker security best practices
- **[CSP Enhancement](CSP_ENHANCEMENT.md)** - Content Security Policy configuration
- **[Authorization Matrix](AUTHORIZATION_MATRIX.md)** - Endpoint access requirements
- **[Security Configuration](SECURITY_CONFIGURATION.md)** - Secrets, OIDC hardening, and proxy settings

### 📖 API Documentation
- **[API Examples](API_EXAMPLES.md)** - Sample requests for all protocols
- **[Model Optimization Guide](MODEL_OPTIMIZATION_GUIDE.md)** - Data model best practices

### Protocol Specification Coverage
- **[Coverage Index](specifications/protocol-coverage.md)** - Entry point for protocol coverage docs
- **[OGC API Features Coverage](specifications/ogc-api-features-coverage.md)** - Operations and query parameter support
- **[OData v4 Coverage](specifications/odata-v4-coverage.md)** - Operations and OData query option support
- **[GeoServices FeatureServer Coverage](feature-server-matrix.md)** - FeatureServer operations and parameter support

## 🎯 By Role

### For New Developers
1. [Getting Started Guide](development/getting-started.md)
2. [Architecture Overview](ARCHITECTURE.md)
3. [Contributing Guide](development/contributing.md)
4. [API Examples](API_EXAMPLES.md)

### For Operations Teams
1. [Performance Monitoring](performance-monitoring.md)
2. [Troubleshooting Guides](troubleshooting/)
3. [Operational Excellence](OPERATIONAL_EXCELLENCE.md)
4. [Backup and Restore](backup-restore.md)
5. [Zero-Downtime Migrations](zero-downtime-migrations.md)
6. [Credential Rotation](credential-rotation.md)

### For System Architects
1. [Architecture Decision Records](adr/)
2. [Clean Architecture Implementation](adr/0012-clean-architecture-implementation.md)
3. [Performance Optimization Strategies](adr/0016-performance-optimization-strategies.md)
4. [Caching Strategy](CACHING_STRATEGY.md)

### For QA Engineers
1. [Testing Excellence Guide](TESTING_EXCELLENCE_GUIDE.md)
2. [CITE Conformance Testing](cite-conformance-testing.md)
3. [Performance Testing](performance-testing.md)
4. [Test Kit Documentation](../tests/Honua.TestKit/README.md)

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
