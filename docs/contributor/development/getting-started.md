# Developer Getting Started Guide

This guide helps new developers set up their development environment and understand the Honua Server architecture for contributing to the project.

## Prerequisites

### Required Software

- **.NET 8.0 SDK** or later
- **PostgreSQL 14+** with PostGIS extension
- **Redis** (optional, for caching - has in-memory fallback)
- **Docker** and **Docker Compose** (recommended for easy setup)
- **Git** for version control

### Optional Development Tools

- **Visual Studio 2022** or **VS Code** with C# extension
- **pgAdmin** or **DBeaver** for database management
- **Postman** or **Insomnia** for API testing
- **Redis CLI** for cache debugging

## Quick Setup with Docker Compose

The fastest way to get started is using Docker Compose:

### 1. Clone and Setup

```bash
# Clone the repository
git clone https://github.com/your-org/honua-server.git
cd honua-server

# Copy environment template
cp .env.example .env

# Edit environment variables
nano .env
```

### 2. Start Development Environment

```bash
# Start PostGIS + Honua Server
docker compose up -d

# Enable Redis (optional)
HONUA_REDIS_URL=redis:6379 docker compose --profile redis up -d

# Enable MinIO (optional S3-compatible storage)
HONUA_STORAGE_PROVIDER=AwsS3 \
HONUA_S3_BUCKET=honua-dev \
HONUA_S3_REGION=us-east-1 \
HONUA_S3_SERVICE_URL=http://minio:9000 \
HONUA_S3_ACCESS_KEY_ID=minioadmin \
HONUA_S3_SECRET_ACCESS_KEY=minioadmin \
docker compose --profile minio up -d

# Check service health
docker compose ps
curl http://localhost:8080/health
```

### 3. Initialize Database

```bash
# Database migrations are run automatically on startup
# Verify schema creation
docker exec -it honua-postgres psql -U postgres -d honua -c "\\dt honua.*"

# Check PostGIS installation
docker exec -it honua-postgres psql -U postgres -d honua -c "SELECT PostGIS_Version();"
```

### 4. Test the API

```bash
# Test health endpoint
curl http://localhost:8080/health | jq .

# Test admin endpoint (OIDC for browser UI; API key is automation-only)
curl -H "X-API-Key: dev-admin-key" http://localhost:8080/admin/configuration | jq .

# Test feature server endpoint
curl "http://localhost:8080/rest/services/1/FeatureServer?f=json" | jq .
```

## Manual Development Setup

For developers who prefer manual setup or need to customize the environment:

### 1. Install Dependencies

**Ubuntu/Debian:**
```bash
# Install .NET 8
wget https://packages.microsoft.com/config/ubuntu/22.04/packages-microsoft-prod.deb
sudo dpkg -i packages-microsoft-prod.deb
sudo apt update
sudo apt install -y dotnet-sdk-8.0

# Install PostgreSQL with PostGIS
sudo apt install -y postgresql-14 postgresql-14-postgis-3 postgresql-client-14

# Install Redis
sudo apt install -y redis-server
```

**macOS:**
```bash
# Install .NET 8
brew install dotnet

# Install PostgreSQL with PostGIS
brew install postgresql@14 postgis

# Install Redis
brew install redis
```

**Windows:**
- Download .NET 8 SDK from https://dotnet.microsoft.com/download
- Install PostgreSQL from https://www.postgresql.org/download/windows/
- Install PostGIS from https://postgis.net/windows_downloads/
- Install Redis from https://redis.io/docs/getting-started/installation/install-redis-on-windows/

### 2. Configure Database

```bash
# Start PostgreSQL
sudo systemctl start postgresql  # Linux
brew services start postgresql@14  # macOS

# Create database and user
sudo -u postgres psql << EOF
CREATE DATABASE honua;
CREATE USER honua_dev WITH ENCRYPTED PASSWORD 'dev_password';
GRANT ALL PRIVILEGES ON DATABASE honua TO honua_dev;
ALTER USER honua_dev CREATEDB;  -- For testing
\q
EOF

# Enable PostGIS
psql -U postgres -d honua << EOF
CREATE EXTENSION IF NOT EXISTS postgis;
CREATE EXTENSION IF NOT EXISTS postgis_topology;
SELECT PostGIS_Version();
\q
EOF
```

### 3. Configure Environment

```bash
# Set environment variables
export ASPNETCORE_ENVIRONMENT=Development
export ConnectionStrings__DefaultConnection="Host=localhost;Database=honua;Username=honua_dev;Password=dev_password"
export HONUA_ADMIN_PASSWORD="dev-admin-key"

# Optional Redis configuration
export Redis__ConnectionString="localhost:6379"
export Cache__Provider="Redis"  # or "InMemory" for local development
```

### 4. Build and Run

```bash
# Restore dependencies
dotnet restore

# Build solution
dotnet build

# Run database migrations
dotnet run --project src/Honua.Server -- --migrate

# Start the server
dotnet run --project src/Honua.Server
```

## Project Structure Overview

Understanding the codebase organization:

```
src/
├── Honua.Server/           # Main application host (ASP.NET Core)
│   ├── Features/           # Feature-based organization
│   │   ├── FeatureServer/  # GeoServices REST API
│   │   ├── OgcFeatures/    # OGC API Features
│   │   ├── OData/          # OData v4 protocol
│   │   ├── Admin/          # Administrative APIs
│   │   ├── Import/         # Data import features
│   │   └── Infrastructure/ # Cross-cutting concerns
│   └── Program.cs          # Application entry point
├── Honua.Core/             # Domain models and abstractions
│   ├── Features/           # Feature contracts
│   ├── Configuration/      # Configuration models
│   └── Exceptions/         # Domain exceptions
├── Honua.Postgres/         # PostgreSQL implementations
│   ├── Features/           # Feature implementations
│   └── Infrastructure/     # Database infrastructure
└── Honua.Admin/            # Blazor WASM admin UI (future)

tests/
├── Honua.TestKit/          # Shared test infrastructure
├── Honua.Server.Tests/     # Integration tests
├── Honua.Core.Tests/       # Unit tests
└── Honua.Architecture.Tests/ # Architecture enforcement tests

docs/
├── user/                   # User documentation (APIs, standards)
├── contributor/            # Contributor docs (architecture, dev, testing)
└── devops/                 # Deployment + operations guides
```

## Development Workflow

### Code Organization Principles

**Vertical Slice Architecture**: Features are organized by business capability, not technical layer.

```csharp
// Good: Feature-based organization
Features/FeatureServer/
├── FeatureServerEndpoints.cs    // API endpoints
├── FeatureServerHandler.cs      // Business logic
├── Models/FeatureServerModels.cs // DTOs
└── Services/GeometryValidator.cs // Supporting services

// Avoid: Layer-based organization
Controllers/    // Cross-feature controllers
Services/       // Mixed business logic
Models/         // Mixed DTOs
```

**Clean Architecture**: Dependency flow follows the rule:
```
Honua.Server (Presentation)
    ↓ depends on
Honua.Postgres (Infrastructure)
    ↓ depends on
Honua.Core (Domain)
```

### Development Best Practices

**1. Test-Driven Development**
```bash
# Write failing test first
dotnet test src/Honua.Server.Tests/Features/FeatureServer/QueryEndpointTests.cs::Query_WithWhereClause_ReturnsFilteredFeatures

# Implement minimum code to pass
# Refactor with confidence
```

**2. Minimal API Pattern**
```csharp
// Preferred: Minimal API endpoints
public static void MapFeatureServerEndpoints(this WebApplication app)
{
    app.MapGet("/rest/services/{serviceId}/FeatureServer/{layerId}/query",
        async (int serviceId, int layerId, QueryRequest request, IFeatureStore store) =>
        {
            // Implementation
        });
}

// Avoid: Controller classes (creates dependency injection issues)
public class FeatureServerController : ControllerBase  // DON'T DO THIS
```

**3. Dependency Injection Limits**
```csharp
// Good: Limited dependencies (≤4 for handlers, ≤5 for endpoints)
public static async Task<IResult> QueryFeatures(
    int serviceId,
    int layerId,
    IFeatureStore store,
    ILayerCatalog catalog,
    ILogger<FeatureServerHandler> logger)  // 3 dependencies - good

// Avoid: Too many dependencies
public class QueryHandler(
    IFeatureStore store,
    ILayerCatalog catalog,
    IGeometryValidator validator,
    ILogger logger,
    IMetrics metrics,
    IEventBus events,
    ICacheService cache)  // 7 dependencies - too many!
```

## Testing Strategy

### Test Categories

**1. Unit Tests** (`Honua.Core.Tests/`)
- Test business logic in isolation
- No external dependencies (database, network)
- Fast execution (<1ms per test)

**2. Integration Tests** (`Honua.Server.Tests/`)
- Test complete features end-to-end
- Use TestContainers for real PostgreSQL + PostGIS
- Test all API endpoints (100% coverage requirement)

**3. Architecture Tests** (`Honua.Architecture.Tests/`)
- Enforce dependency direction rules
- Validate public API surface
- Check for anti-patterns

### Running Tests

```bash
# Run all tests
dotnet test

# Run specific test category
dotnet test --filter Category=Unit
dotnet test --filter Category=Integration

# Run with coverage
dotnet test --collect:"XPlat Code Coverage"
dotnet test --logger trx --results-directory test-results/

# Run architecture tests
dotnet test tests/Honua.Architecture.Tests/
```

### Writing Tests

**Integration Test Example:**
```csharp
[Collection("Database")]
[Protocol(Protocols.FeatureServer)]
public class QueryEndpointTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres;
    private readonly WebApplicationFactory<Program> _factory;

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task Query_WithWhereClause_ReturnsFilteredFeatures()
    {
        // Arrange
        var client = _factory.CreateClient();
        await SeedTestData();

        // Act
        var response = await client.GetAsync("/rest/services/1/FeatureServer/0/query?where=name='Test'&f=json");

        // Assert
        response.Should().BeSuccessful();
        var content = await response.Content.ReadFromJsonAsync<FeatureSet>();
        content!.Features.Should().HaveCount(1);
        content.Features[0].Attributes["name"].Should().Be("Test");
    }
}
```

## Development Tools Setup

### VS Code Configuration

Create `.vscode/settings.json`:
```json
{
  "dotnet.defaultSolution": "Honua.sln",
  "omnisharp.enableRoslynAnalyzers": true,
  "omnisharp.enableEditorConfigSupport": true,
  "csharp.format.enable": true,
  "csharp.semanticHighlighting.enabled": true,
  "files.exclude": {
    "**/bin": true,
    "**/obj": true
  }
}
```

Create `.vscode/tasks.json`:
```json
{
  "version": "2.0.0",
  "tasks": [
    {
      "label": "build",
      "command": "dotnet",
      "type": "process",
      "args": ["build"],
      "group": "build"
    },
    {
      "label": "test",
      "command": "dotnet",
      "type": "process",
      "args": ["test"],
      "group": "test"
    },
    {
      "label": "migrate",
      "command": "dotnet",
      "type": "process",
      "args": ["run", "--project", "src/Honua.Server", "--", "--migrate"]
    }
  ]
}
```

### Database Development Tools

**pgAdmin Connection:**
- Host: localhost
- Port: 5432
- Database: honua
- Username: postgres (or honua_dev)
- Password: (from your configuration)

**Useful SQL Queries:**
```sql
-- Check layer catalog
SELECT * FROM honua.layers ORDER BY created_at DESC;

-- Check recent features
SELECT layer_id, COUNT(*) as feature_count
FROM honua.features
GROUP BY layer_id;

-- Check PostGIS functions
SELECT name FROM pg_proc WHERE proname LIKE 'st_%' LIMIT 10;

-- Performance monitoring
SELECT * FROM pg_stat_activity WHERE state = 'active';
```

## Debugging and Development Tips

### Application Debugging

**1. Enable Detailed Logging:**
```bash
export Logging__LogLevel__Default=Debug
export Logging__LogLevel__Honua=Trace
export Logging__LogLevel__Microsoft.AspNetCore=Information
```

**2. Use Developer Exception Pages:**
```bash
export ASPNETCORE_ENVIRONMENT=Development
# Automatically enables detailed error pages
```

**3. Database Query Logging:**
```bash
# Enable EF Core query logging
export Logging__LogLevel__Microsoft.EntityFrameworkCore.Database.Command=Information
```

### Performance Profiling

```bash
# Use dotnet-trace for performance profiling
dotnet tool install --global dotnet-trace

# Collect performance trace
dotnet-trace collect -p $(pgrep dotnet) --format speedscope

# Analyze memory usage
dotnet tool install --global dotnet-gcdump
dotnet-gcdump collect -p $(pgrep dotnet)
```

### Common Development Issues

**Issue: Tests failing with database connection errors**
```bash
# Ensure PostgreSQL is running
systemctl status postgresql
docker ps | grep postgres

# Check connection string
echo $ConnectionStrings__DefaultConnection

# Reset test database
dropdb -U postgres honua_test 2>/dev/null || true
createdb -U postgres honua_test
```

**Issue: Build failing with package restore errors**
```bash
# Clear NuGet cache
dotnet nuget locals all --clear

# Restore packages
dotnet restore --force

# Clean and rebuild
dotnet clean
dotnet build
```

**Issue: Hot reload not working**
```bash
# Enable hot reload
dotnet watch run --project src/Honua.Server

# Alternative with specific environment
ASPNETCORE_ENVIRONMENT=Development dotnet watch run --project src/Honua.Server
```

## Contributing Guidelines

### Code Style

**1. Run Code Formatting:**
```bash
# Format code before committing (enforced by CI)
dotnet format Honua.sln

# Check for formatting issues
dotnet format Honua.sln --verify-no-changes
```

**2. Follow Naming Conventions:**
```csharp
// Use PascalCase for public members
public class LayerDefinition { }
public async Task<LayerDefinition> GetLayerAsync(int id) { }

// Use camelCase for private fields and parameters
private readonly IFeatureStore _featureStore;
public LayerService(IFeatureStore featureStore) { }

// Use descriptive names
var layerWithGeometry = await store.GetLayerAsync(layerId);  // Good
var l = await store.GetLayerAsync(layerId);  // Avoid
```

### Git Workflow

**1. Branch Naming:**
```bash
# Feature branches
git checkout -b feature/add-spatial-filtering

# Bug fixes
git checkout -b fix/geometry-validation-error

# Documentation
git checkout -b docs/update-api-examples
```

**2. Commit Messages:**
```bash
# Use conventional commits
git commit -m "feat: add spatial filtering for feature queries"
git commit -m "fix: resolve geometry validation edge case"
git commit -m "docs: update API examples for OData endpoints"
git commit -m "test: add integration tests for import service"
```

**3. Pull Request Checklist:**
- [ ] Code formatted with `dotnet format`
- [ ] All tests pass (`dotnet test`)
- [ ] Architecture tests pass
- [ ] New features have integration tests
- [ ] Public APIs have XML documentation
- [ ] Breaking changes documented
- [ ] Performance impact considered

## Next Steps

After completing the setup:

1. **Explore the Codebase:**
   - Read existing ADRs in `docs/contributor/adr/`
   - Review test examples in `tests/Honua.Server.Tests/`
   - Understand feature organization in `src/Honua.Server/Features/`

2. **Try Development Tasks:**
   - Add a simple endpoint to an existing feature
   - Write integration tests for the new endpoint
   - Run the full test suite

3. **Join the Community:**
   - Read the contributing guidelines
   - Check GitHub issues for "good first issue" labels
   - Ask questions in project discussions

## Getting Help

**Documentation:**
- Architecture Decision Records: `docs/contributor/adr/`
- Troubleshooting guides: `docs/devops/troubleshooting/`
- API examples: `docs/user/API_EXAMPLES.md`

**Development Support:**
- GitHub Issues for bugs and features
- GitHub Discussions for questions
- Code reviews for learning opportunities

**Quick Reference Commands:**
```bash
# Development workflow
docker compose up -d      # Start dev environment
dotnet test              # Run tests
dotnet format Honua.sln  # Format code
git commit -m "feat: ..." # Commit changes

# Debugging
docker logs honua-server  # Application logs
curl http://localhost:8080/health  # Health check
psql -h localhost -U postgres -d honua  # Database access
```
