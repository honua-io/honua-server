# Honua Deployment Guide

This guide covers deployment procedures for all Honua components across the CI/CD pipeline.

## Prerequisites

### Required Secrets

Each repository requires the following secrets to be configured in GitHub:

#### honua-server
```bash
# NuGet publishing
NUGET_API_KEY=<nuget-api-key>

# Container registry (if using private registry)
DOCKER_USERNAME=<docker-username>
DOCKER_PASSWORD=<docker-password>

# Cloud providers
AWS_ROLE_ARN=<aws-role-arn>
AZURE_CLIENT_ID=<azure-client-id>
AZURE_TENANT_ID=<azure-tenant-id>
AZURE_SUBSCRIPTION_ID=<azure-subscription-id>

# Infrastructure cost analysis
INFRACOST_API_KEY=<infracost-api-key>

# LLM architecture review (optional)
OPENAI_API_KEY=<openai-api-key>
```

#### honua-core-sdk
```bash
NUGET_API_KEY=<nuget-api-key>
```

#### honua-admin-tools
```bash
NUGET_API_KEY=<nuget-api-key>
NPM_TOKEN=<npm-token>
PYPI_API_TOKEN=<pypi-api-token>
```

#### geospatial-grpc
```bash
BUF_TOKEN=<buf-build-token>
```

### Repository Setup

1. **Copy Workflow Files**
   ```bash
   # For honua-core-sdk repository
   cp .github/workflows/honua-core-sdk-ci.yml ../honua-core-sdk/.github/workflows/ci.yml

   # For honua-admin-tools repository
   cp .github/workflows/honua-admin-tools-ci.yml ../honua-admin-tools/.github/workflows/ci.yml

   # For geospatial-grpc repository
   cp .github/workflows/geospatial-grpc-ci.yml ../geospatial-grpc/.github/workflows/ci.yml
   cp buf.gen.*.yaml ../geospatial-grpc/
   ```

2. **Configure Branch Protection**
   ```bash
   # Enable branch protection for main/trunk branches
   # - Require status checks to pass
   # - Require branches to be up to date
   # - Include administrators
   # - Restrict pushes to matching branches
   ```

## Deployment Workflows

### 1. Development Workflow

#### Feature Development
```bash
# 1. Create feature branch
git checkout -b feature/my-feature

# 2. Make changes and commit
git add .
git commit -m "feat: implement new feature"

# 3. Push branch (triggers CI)
git push origin feature/my-feature

# 4. Create pull request
# - CI/CD pipeline runs automatically
# - Architecture review (if configured)
# - All quality gates must pass
```

#### Pull Request Process
1. **Automated Checks**
   - Build and test execution
   - Security scanning
   - Code coverage validation
   - Format verification

2. **Review Process**
   - LLM architecture review (automated)
   - Human code review (required)
   - Quality gate validation

3. **Merge to Trunk**
   - All checks pass
   - Approved by reviewers
   - Automatic deployment to staging (if configured)

### 2. Release Workflow

#### Package Release Process

**For honua-core-sdk:**
```bash
# 1. Create release tag
git tag v1.2.3
git push origin v1.2.3

# 2. Create GitHub release
# - Triggers automatic NuGet publishing
# - Generates release notes
# - Creates downloadable packages
```

**For honua-admin-tools:**
```bash
# 1. Use workflow dispatch for controlled release
# GitHub Actions → Honua Admin Tools CI/CD → Run workflow
# - Set version: "1.2.3"
# - Choose packages to publish:
#   ✓ Publish .NET packages
#   ✓ Publish NPM packages
#   ✓ Publish PyPI packages

# 2. Monitor publishing progress
# - .NET packages → NuGet.org
# - Node.js packages → NPM registry
# - Python packages → PyPI
```

**For geospatial-grpc:**
```bash
# 1. Create release with protocol version
git tag v1.2.3
git push origin v1.2.3

# 2. Automatic publishing
# - Buf Schema Registry
# - Generated client packages
# - Documentation updates
```

#### Infrastructure Deployment

**Terraform Deployment:**
```bash
# 1. Use workflow dispatch for infrastructure changes
# GitHub Actions → Terraform CI/CD → Run workflow
# - Environment: "dev" | "staging" | "prod"
# - Apply: true (for actual deployment)

# 2. Review deployment plan
# - Cost analysis comment on PR
# - Security scan results
# - Infrastructure changes summary

# 3. Manual approval (for production)
# - Review all changes
# - Confirm deployment window
# - Approve deployment
```

### 3. Production Deployment

#### Server Deployment
```bash
# 1. Tag server release
git tag server-v1.2.3
git push origin server-v1.2.3

# 2. Manual deployment (recommended)
# - Review all dependent package versions
# - Verify infrastructure readiness
# - Deploy in maintenance window

# 3. Post-deployment validation
# - Health check verification
# - Integration test execution
# - Performance monitoring
```

## Environment Configuration

### Development Environment
- **Purpose**: Feature development and testing
- **Deployment**: Automatic on trunk merge
- **Database**: Shared development instance
- **Monitoring**: Basic logging and metrics

### Staging Environment
- **Purpose**: Pre-production validation
- **Deployment**: Manual approval required
- **Database**: Production-like dataset
- **Monitoring**: Full monitoring stack

### Production Environment
- **Purpose**: Live user traffic
- **Deployment**: Manual approval + change window
- **Database**: Production instance with backups
- **Monitoring**: Full monitoring with alerting

## Rollback Procedures

### Package Rollback

**NuGet Packages:**
```bash
# 1. Unlist problematic version
dotnet nuget delete Honua.Core 1.2.3 \
  --api-key $NUGET_API_KEY \
  --source https://api.nuget.org/v3/index.json

# 2. Publish hotfix version
git tag v1.2.4
git push origin v1.2.4
```

**NPM Packages:**
```bash
# 1. Deprecate problematic version
npm deprecate @honua/admin-tools@1.2.3 "Critical bug - use 1.2.4"

# 2. Publish hotfix
npm publish --tag latest
```

### Infrastructure Rollback

**Terraform Rollback:**
```bash
# 1. Revert to previous configuration
git revert <commit-hash>

# 2. Deploy previous version
# GitHub Actions → Terraform CI/CD → Run workflow
# - Environment: "production"
# - Apply: true
```

### Server Rollback

**Docker Deployment Rollback:**
```bash
# 1. Deploy previous image version
docker run -d honua-server:previous-tag

# 2. Update load balancer configuration
# 3. Verify service health
# 4. Monitor for issues
```

## Monitoring and Alerting

### Build Monitoring
- **Failed Builds**: Immediate Slack notification
- **Security Issues**: Email + Slack alerts
- **Performance Regression**: Weekly reports

### Deployment Monitoring
- **Deployment Success**: Status dashboard
- **Health Checks**: Automated monitoring
- **Performance Metrics**: Real-time dashboards

### Package Monitoring
- **Download Metrics**: Monthly reports
- **Version Adoption**: Quarterly analysis
- **Security Vulnerabilities**: Immediate alerts

## Troubleshooting

### Common Build Failures

1. **Test Failures**
   ```bash
   # Check test logs
   dotnet test --verbosity detailed

   # Run specific test
   dotnet test --filter "TestMethodName"

   # Check test database
   docker-compose up -d postgres
   ```

2. **Package Publishing Failures**
   ```bash
   # Verify API keys
   dotnet nuget push --help

   # Check package validation
   dotnet pack --verbosity detailed

   # Test local package installation
   dotnet add package Honua.Core --source ./packages
   ```

3. **Docker Build Failures**
   ```bash
   # Local build test
   docker build -t test-image .

   # Check build context
   docker build --progress=plain .

   # Verify base images
   docker pull mcr.microsoft.com/dotnet/runtime:10.0
   ```

### Infrastructure Issues

1. **Terraform Plan Failures**
   ```bash
   # Validate configuration
   terraform validate

   # Check provider versions
   terraform version

   # Review state file
   terraform state list
   ```

2. **Cloud Provider Access**
   ```bash
   # Test AWS credentials
   aws sts get-caller-identity

   # Test Azure credentials
   az account show

   # Verify permissions
   aws iam get-role --role-name <role-name>
   ```

## Security Considerations

### Secret Management
- **Rotation**: Quarterly secret rotation
- **Access Control**: Least privilege principle
- **Audit**: Regular access review

### Vulnerability Management
- **Scanning**: Automated vulnerability scanning
- **Patching**: Monthly security updates
- **Monitoring**: Continuous security monitoring

### Compliance
- **Logging**: Comprehensive audit logging
- **Backup**: Regular backup verification
- **Recovery**: Disaster recovery testing

## Performance Optimization

### Build Performance
- **Parallel Execution**: Multi-core test execution
- **Caching**: Aggressive dependency caching
- **Resource Allocation**: Optimized runner resources

### Deployment Performance
- **Blue-Green Deployment**: Zero-downtime deployments
- **Canary Releases**: Gradual rollout strategy
- **Load Balancing**: Intelligent traffic routing

## Maintenance

### Regular Tasks
- **Weekly**: Build pipeline health check
- **Monthly**: Dependency updates
- **Quarterly**: Security review
- **Annually**: Infrastructure cost optimization

### Capacity Planning
- **Build Resources**: Monitor runner usage
- **Storage**: Package storage monitoring
- **Network**: Bandwidth optimization