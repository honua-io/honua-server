# Critical Fix Test Coverage Guide

This document outlines the comprehensive test coverage for critical fixes implemented in the Honua server. The test suite validates security, performance, memory management, and resilience pattern fixes.

## Test Structure Overview

```
tests/Honua.Server.Tests/
├── Features/Security/CriticalSecurityFixTests.cs       # Security vulnerability fixes
├── Performance/CriticalPerformanceFixTests.cs          # Database and cache performance
├── Features/Infrastructure/MemoryManagementTests.cs    # Memory leak prevention
├── Features/Infrastructure/ResiliencePatternTests.cs   # Circuit breakers, rate limiting
├── LoadTests/CriticalFixLoadTests.cs                   # Production-scale load testing
├── Integration/CriticalFixIntegrationTests.cs          # End-to-end workflow validation
└── CriticalFixTestSuite.cs                            # Test orchestration and baselines
```

## Critical Fixes Covered

### 🔒 Security Fixes (P0/P1)
- **Environment Validation**: Prevents development auth bypass in production
- **HTTPS Enforcement**: Blocks basic auth over HTTP in production environments  
- **Password Complexity**: Enforces strong password requirements in production
- **Credential Sanitization**: Prevents JWT/secret exposure in logs and telemetry
- **SQL Injection Prevention**: Tests malicious field names and CQL filter inputs
- **CORS Security**: Validates proper origin restrictions
- **Authorization Bypass Prevention**: Tests authentication boundary enforcement

### ⚡ Performance Fixes
- **Database Index Effectiveness**: Validates spatial query optimization
- **N+1 Query Prevention**: Tests relationship loading efficiency
- **Cache Performance**: Background refresh and concurrent access optimization
- **Redis SCAN Operations**: Replaces blocking KEYS operations with paginated SCAN
- **Connection Pool Management**: High concurrency handling without exhaustion
- **Query Plan Caching**: Performance improvement validation
- **Bulk Operation Efficiency**: Batching and throughput optimization

### 💾 Memory Management
- **Cache Bounds Enforcement**: Memory limits under pressure
- **Import Service Memory**: Stable usage during large file processing
- **Object Pool Efficiency**: Allocation reduction validation
- **Memory Leak Detection**: Long-running scenario testing
- **Garbage Collection Optimization**: Pressure handling and cleanup verification
- **Cache Eviction Policies**: Proper memory management under load

### 🛡️ Resilience Patterns
- **Circuit Breaker Activation**: Failure threshold and fast-fail behavior
- **Rate Limiting Enforcement**: Request throttling and protection
- **Connection Pool Graceful Degradation**: Exhaustion handling
- **File Upload Backpressure**: Memory exhaustion prevention
- **External Service Fallback**: Graceful degradation when dependencies fail
- **System Stability**: Sustained error condition handling

## Test Execution

### Quick Validation (2-3 minutes)
Run security and basic performance tests:
```bash
# Security tests only
dotnet test --filter "Category=SecurityTest" --logger "console;verbosity=normal"

# Basic performance validation
dotnet test tests/Honua.Server.Tests/Performance/ --logger "console;verbosity=normal"
```

### Comprehensive Validation (30 minutes)
Full test suite with all categories:
```bash
# All critical fix tests
dotnet test tests/Honua.Server.Tests/ --filter "FullyQualifiedName~CriticalFix" --logger "console;verbosity=normal"

# Specific category execution
dotnet test --filter "FullyQualifiedName~Memory" --logger "console;verbosity=normal"
dotnet test --filter "FullyQualifiedName~Resilience" --logger "console;verbosity=normal"
dotnet test --filter "FullyQualifiedName~LoadTests" --logger "console;verbosity=normal"
```

### Production Validation (15 minutes)
High-confidence subset for production deployment:
```bash
# Security + Integration + Load (critical path)
dotnet test --filter "(Category=SecurityTest|Category=IntegrationTest|FullyQualifiedName~LoadTests)" \
  --logger "console;verbosity=normal"
```

### CI/CD Integration
For automated testing pipelines:
```bash
# Parallel execution by category
dotnet test --filter "Category=SecurityTest" --parallel & 
dotnet test --filter "FullyQualifiedName~Performance" --parallel &
dotnet test --filter "FullyQualifiedName~Memory" --parallel &
wait

# Integration and load tests sequentially
dotnet test --filter "Category=IntegrationTest" --logger "console;verbosity=normal"
dotnet test --filter "FullyQualifiedName~LoadTests" --logger "console;verbosity=normal"
```

## Expected Results

### Security Tests
- ✅ All authentication bypass attempts in production should fail
- ✅ SQL injection payloads should be safely handled or rejected
- ✅ Credential patterns should be sanitized from logs
- ✅ CORS should properly restrict malicious origins
- ✅ HTTPS enforcement should block insecure basic auth

### Performance Tests
- ✅ Spatial queries should use database indexes (< 5s for large datasets)
- ✅ N+1 queries should be prevented (≤ 5 DB queries for 100 related features)
- ✅ Cache refresh should handle concurrent access without stampede
- ✅ Redis SCAN should complete efficiently (< 500ms for 1000 keys)
- ✅ Connection pool should handle 200+ concurrent operations

### Memory Tests
- ✅ Cache memory should be bounded (< 500MB increase under pressure)
- ✅ Import operations should remain stable (< 1GB peak, < 200MB retained)
- ✅ Object pooling should reduce GC pressure (< 50 gen0 collections for 100 ops)
- ✅ No significant memory leaks (< 50MB growth after concurrent operations)

### Resilience Tests
- ✅ Circuit breaker should activate after repeated failures (< 100ms fast-fail)
- ✅ Rate limiting should protect against request floods
- ✅ Connection pool exhaustion should degrade gracefully (< 10% timeouts)
- ✅ File upload backpressure should prevent memory exhaustion
- ✅ System should maintain 60%+ success rate under mixed failure conditions

### Load Tests
- ✅ 100 concurrent users: < 2s average response, < 5% error rate
- ✅ Authentication load: < 500ms average auth time under attack
- ✅ Cache performance: Measurable hit ratio benefits
- ✅ Database connections: Stable performance over 3+ minutes
- ✅ Memory stability: < 1GB growth during bulk operations

### Integration Tests  
- ✅ Complete workflows (auth → import → query → export) work securely
- ✅ Security fixes prevent cross-endpoint attack chains
- ✅ Performance optimizations work together under load
- ✅ Resilience patterns prevent cascading failures
- ✅ Memory management prevents OOM in complex scenarios

## Troubleshooting

### Common Issues
1. **Database Connection Timeouts**: Check connection pool configuration
2. **Redis Unavailable**: Tests gracefully skip Redis-dependent validations  
3. **Memory Pressure**: Ensure test environment has sufficient RAM (4GB+ recommended)
4. **Performance Variance**: Run multiple iterations and check for consistency

### Test Environment Requirements
- PostgreSQL with PostGIS extension
- Redis (optional, tests skip if unavailable)
- 4GB+ available RAM
- Multi-core processor for load tests

### Debugging Failed Tests
```bash
# Verbose output for specific test
dotnet test --filter "DisplayName~specific_test_name" \
  --logger "console;verbosity=diagnostic"

# Memory profiling during tests
dotnet-counters monitor -p $(pgrep dotnet) --counters Microsoft.AspNetCore.Hosting
```

## Coverage Reports

Generate detailed coverage reports:
```bash
# Install coverage tools
dotnet tool install --global dotnet-reportgenerator-globaltool

# Generate coverage
dotnet test --collect:"XPlat Code Coverage" --filter "FullyQualifiedName~CriticalFix"

# Generate HTML report
reportgenerator -reports:"TestResults/*/coverage.cobertura.xml" \
  -targetdir:"TestResults/CoverageReport" -reporttypes:Html
```

## Performance Baselines

Baseline performance metrics for regression detection:

| Operation | Target | Baseline |
|-----------|--------|----------|
| Health Check | < 100ms | ~50ms |
| Metadata Query | < 500ms | ~200ms |
| Simple Feature Query | < 1s | ~400ms |
| Spatial Query (1k features) | < 2s | ~800ms |
| Authentication | < 200ms | ~100ms |
| Cache Hit | < 50ms | ~20ms |
| Database Connection | < 1s | ~100ms |

## Maintenance

### Adding New Tests
1. Follow existing patterns and naming conventions
2. Use appropriate test attributes (`[SecurityTest]`, `[IntegrationTest]`)
3. Include performance assertions where relevant
4. Update this guide with new test coverage

### Updating Baselines
Regenerate performance baselines after infrastructure changes:
```bash
dotnet test --filter "DisplayName~Performance benchmarks establish baseline metrics" \
  --logger "console;verbosity=normal"
```

---

**Note**: This test suite provides comprehensive validation of critical fixes but should be complemented with production monitoring and observability for complete confidence in system reliability.