# DevOps Documentation

This section is for installing, configuring, operating, and upgrading Honua in production.

## 🚀 **Deployment**

**Deployment Scenarios:**
- [**Deployment Scenarios**](DEPLOYMENT_SCENARIOS.md) — specific deployment patterns for different use cases
- **Infrastructure Templates** - Docker Compose, Helm, and Terraform are in `infrastructure/`
- [**Serverless Deployments**](serverless-deployments.md) — AWS Lambda and Azure Functions
- [**Container Images**](CONTAINER_IMAGES.md) — registries, tags, and publishing

**Configuration:**
- [**Admin UI Setup**](ADMIN_UI.md) — web interface deployment
- [**Security Configuration**](SECURITY_CONFIGURATION.md) — OIDC, secrets, and proxy settings

## 🔧 **Operations**

**Daily Operations:**
- [**Operational Excellence**](OPERATIONAL_EXCELLENCE.md) — current operational tooling and practices
- [**Troubleshooting**](TROUBLESHOOTING.md) — common issues and solutions
- [**Runbooks**](runbooks/README.md) — incident response playbooks

**Maintenance:**
- [**Backup and Restore**](backup-restore.md) — data protection strategies
- [**Zero Downtime Migrations**](zero-downtime-migrations.md) — database migration strategies
- [**Credential Rotation**](credential-rotation.md) — security credential management

## 📊 **Monitoring & Performance**

**Performance Monitoring:**
- [**Performance Monitoring**](performance-monitoring.md) — observability setup and metrics
- [**Performance Testing**](performance-testing.md) — load testing strategies
- [**Load & Soak Testing**](load-soak-testing.md) — comprehensive performance validation

**Optimization:**
- [**Query Optimization**](query-optimization.md) — database and spatial query performance
- [**Connection Pool Sizing**](connection-pool-sizing.md) — database connection optimization
- [**Memory Optimizations Report**](MEMORY_OPTIMIZATIONS_REPORT.md) — memory usage analysis

**Caching:**
- [**Caching Strategy**](CACHING_STRATEGY.md) — comprehensive caching approach
- [**Caching Quick Reference**](CACHING_QUICK_REFERENCE.md) — configuration examples
- [**Database Query Caching**](DATABASE_QUERY_CACHING.md) — query result caching

## 🔐 **Security**

- [**Security Configuration**](SECURITY_CONFIGURATION.md) — authentication, authorization, and security settings
- [**Authorization Matrix**](AUTHORIZATION_MATRIX.md) — endpoint access requirements
- [**Container Security**](CONTAINER_SECURITY.md) — container hardening and best practices
- [**CSP Enhancement**](CSP_ENHANCEMENT.md) — Content Security Policy configuration

## 🏗️ **Architecture**

- [**Architecture Diagrams**](../contributor/ARCHITECTURE_DIAGRAMS.md) — visual system design documentation
