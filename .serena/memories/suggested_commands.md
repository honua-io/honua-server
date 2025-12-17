# Essential Honua Server Commands

## Core Development Commands

### Build & Run
```bash
# Restore dependencies
dotnet restore Honua.sln

# Build (warnings as errors enforced)
dotnet build Honua.sln --configuration Release

# Run the server (Phase 0 - minimal endpoints)
dotnet run --project src/Honua.Server

# Build with verbose output for debugging
dotnet build Honua.sln --verbosity normal
```

### Testing
```bash
# Run all tests
dotnet test

# Run tests with coverage
dotnet test --collect:"XPlat Code Coverage"

# Run only unit tests (fast feedback)
dotnet test --filter "Category!=Integration&Category!=Slow"

# Run specific protocol tests
dotnet test --filter "Protocol=FeatureServer"
dotnet test --filter "Protocol=OgcFeatures"

# Run tests in watch mode (development)
dotnet watch test --project tests/Honua.Core.Tests

# Run with parallel execution (CI)
dotnet test -- RunConfiguration.MaxCpuCount=0
```

### Code Quality
```bash
# Format code (required before commit)
dotnet format Honua.sln

# Verify formatting without changes
dotnet format Honua.sln --verify-no-changes

# Format with diagnostic verbosity
dotnet format Honua.sln --verbosity diagnostic

# Analyze code (runs automatically on build)
dotnet build --verbosity normal  # Shows analyzer warnings
```

### Native AOT Build
```bash
# Publish AOT binary (production)
dotnet publish src/Honua.Server -c Release -p:PublishAot=true

# Publish for specific runtime
dotnet publish src/Honua.Server -c Release \
  --runtime linux-musl-x64 \
  -p:PublishAot=true \
  -p:StripSymbols=true
```

## Development Workflow Commands

### Local Development (Current - Phase 0)
```bash
# Start basic server
dotnet run --project src/Honua.Server
# Endpoints: http://localhost:5000/healthz/live, /healthz/ready
```

### Planned Local Development (Post-Phase 0)
```bash
# Start with Aspire orchestration (planned)
cd src/Honua.AppHost
dotnet run
# Opens Aspire dashboard with Honua + PostgreSQL + Redis
```

### Docker Commands (Planned)
```bash
# Build Docker image
docker build -f docker/Dockerfile -t honua-server .

# Run with AOT image
docker run -p 8080:8080 \
  -e ConnectionStrings__DefaultConnection="Host=postgres;Database=honua;Username=postgres;Password=postgres" \
  -e HONUA_ADMIN_PASSWORD="change-me" \
  honua-server:latest
```

## Git & CI Commands

### Git Workflow
```bash
# Check status
git status

# Add all changes
git add .

# Commit with conventional format
git commit -m "feat: add query endpoint (#12)"

# Check instruction sync before commit
bash scripts/check-instructions-sync.sh

# Push changes
git push
```

### CI Commands (run in GitHub Actions)
```bash
# Full CI build
dotnet restore Honua.sln
dotnet build Honua.sln --no-restore --configuration Release /p:TreatWarningsAsErrors=true
dotnet format Honua.sln --verify-no-changes
dotnet test --configuration Release --collect:"XPlat Code Coverage"
```

## Database Commands (Planned)

### Database Migrations (Planned - DbUp)
```bash
# Apply migrations (planned)
dotnet run --project src/Honua.Server -- --migrate

# Check migration status (planned)
dotnet run --project src/Honua.Server -- --migrate --dry-run
```

### PostgreSQL Commands (External)
```bash
# Connect to PostgreSQL
psql -h localhost -U postgres -d honua

# Check PostGIS version
psql -h localhost -U postgres -d honua -c "SELECT PostGIS_Version();"

# List spatial tables
psql -h localhost -U postgres -d honua -c "\dt spatial.*"
```

## File System Commands (Linux)

### Project Navigation
```bash
# Project root
cd /home/mike/projects/honua-server

# Source code
ls src/                    # View source projects
ls src/Honua.Server/       # Main server project
ls tests/                  # Test projects

# Documentation  
ls docs/                   # Architecture docs
ls docs/adr/               # Architecture decision records
```

### File Search & Content
```bash
# Find files by name
find . -name "*.cs" -type f | grep -v bin | grep -v obj

# Search code content
grep -r "FeatureServer" src/ --include="*.cs"
grep -r "TODO" . --include="*.cs" --include="*.md"

# List recent changes
git log --oneline -10
git diff --name-only HEAD~1
```

## Performance & Benchmarking (Planned)

### Benchmarks (Planned - BenchmarkDotNet)
```bash
# Run performance benchmarks
dotnet run --project benchmarks/Honua.Benchmarks -c Release

# Run specific benchmark
dotnet run --project benchmarks/Honua.Benchmarks -c Release -- --filter "*QueryBenchmarks*"

# Run memory soak tests
dotnet run --project benchmarks/Honua.Benchmarks -c Release -- --filter "*MemorySoak*"
```

## Package Management

### NuGet Commands
```bash
# List packages
dotnet list package

# Check for vulnerabilities
dotnet list package --vulnerable --include-transitive

# Update packages
dotnet add package PackageName
dotnet remove package PackageName

# Restore with locked mode (CI)
dotnet restore --locked-mode
```

## Troubleshooting Commands

### Build Issues
```bash
# Clean solution
dotnet clean Honua.sln

# Clean and rebuild
dotnet clean && dotnet build

# Verbose build for diagnostics  
dotnet build Honua.sln --verbosity detailed

# Check for build warnings
dotnet build 2>&1 | grep warning
```

### Runtime Issues
```bash
# Run with verbose logging
dotnet run --project src/Honua.Server --verbosity normal

# Check health endpoints
curl http://localhost:5000/healthz/live
curl http://localhost:5000/healthz/ready

# View logs
journalctl -u honua-server -f   # systemd logs
docker logs honua-server -f     # container logs
```

## Editor Integration

### VS Code
```bash
# Open project in VS Code
code .

# Recommended extensions (planned)
# - C# Dev Kit
# - GitLens
# - EditorConfig for VS Code
```

## Summary
The most commonly used commands for daily development:
1. `dotnet run --project src/Honua.Server` - Start server
2. `dotnet test` - Run tests  
3. `dotnet format Honua.sln` - Format code
4. `dotnet build` - Build with quality checks
5. `git add . && git commit -m "type: description"` - Commit changes