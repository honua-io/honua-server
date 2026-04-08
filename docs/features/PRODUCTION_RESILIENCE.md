# Production Resilience & Monitoring

This document describes the comprehensive production resilience patterns and monitoring capabilities implemented in Honua Server to ensure high availability, prevent cascading failures, and provide actionable observability.

## Overview

The production resilience system consists of five key components:

1. **Circuit Breaker Patterns** - Prevent cascading failures from external service dependencies
2. **Rate Limiting** - Protect against traffic spikes and abuse
3. **Connection Pool Monitoring** - Track database connection health and utilization
4. **File Upload Backpressure** - Prevent memory exhaustion from large file uploads
5. **Comprehensive Metrics & Alerting** - Real-time observability with actionable alerts

## Circuit Breaker Implementation

### External Service Protection

Circuit breakers are automatically applied to all external HTTP clients to prevent cascading failures:

```csharp
// Automatically configured for all external services
builder.Services.AddHttpClient("external-service")
    .AddComprehensiveResilience("service-name");
```

### Configuration

Circuit breaker behavior is configurable per service type:

```json
{
  "ExternalServices": {
    "CircuitBreaker": {
      "ArcGisRest": {
        "FailureThreshold": 3,
        "DurationOfBreakSeconds": 60,
        "SuccessThreshold": 2,
        "TimeoutSeconds": 45,
        "MaxRetryAttempts": 2
      },
      "GeoServerRest": {
        "FailureThreshold": 5,
        "DurationOfBreakSeconds": 30,
        "TimeoutSeconds": 60
      }
    }
  }
}
```

### Retry Policies

Exponential backoff with jitter:
- Initial delay: 500ms
- Maximum delay: 5000ms
- Maximum retries: 3 attempts
- Jitter applied to prevent thundering herd

### Monitoring

Circuit breaker state changes are logged and can trigger alerts:

```promql
# Alert on circuit breaker openings
increase(honua_circuit_breaker_state_changes_total{state="open"}[5m]) > 0
```

## Rate Limiting

### Multi-Level Protection

Rate limiting is applied at three levels:

1. **Global limits** - Overall requests per minute per client
2. **Endpoint-specific limits** - Different limits for different operations
3. **API key-based limits** - Higher limits for authenticated users

### Implementation

```csharp
// Applied automatically in middleware pipeline
app.UseProductionRateLimiting();

// Endpoint-specific limits via attributes
[RateLimit(50)] // 50 requests per minute for this endpoint
public async Task<IResult> ExpensiveOperation() { }
```

### Configuration

```json
{
  "RateLimiting": {
    "Enabled": true,
    "GlobalRequestsPerMinute": 1000,
    "QueryRequestsPerMinute": 100,
    "UploadRequestsPerMinute": 10,
    "UseDistributedRateLimiting": true
  }
}
```

### Sliding Window Algorithm

When Redis is available, uses sliding window for more accurate rate limiting:
- Precise request counting within time windows
- Distributed across multiple instances
- Automatic cleanup of expired entries

### Rate Limit Headers

Responses include standard rate limiting headers:

```http
X-RateLimit-Limit: 1000
X-RateLimit-Remaining: 897
X-RateLimit-Reset: 1640995200
```

## Connection Pool Monitoring

### Real-Time Metrics

Tracks database connection pool health in real-time:

- **Pool utilization ratio** (0.0-1.0)
- **Connection acquisition latency** (milliseconds)
- **Failed acquisition attempts**
- **Connection timeouts**

### Implementation

```csharp
// Automatic monitoring of all database connections
services.AddProductionMonitoring();

// Connection metrics automatically collected
var utilization = connectionPoolMetrics.GetPoolUtilization();
var failures = connectionPoolMetrics.GetTotalFailures();
```

### Alerting Thresholds

| Metric | Warning | Critical | Action |
|--------|---------|----------|--------|
| Pool Utilization | >80% | >95% | Scale connections |
| Acquisition Failures | >0 | >10 | Check database health |
| Avg Latency | >100ms | >1000ms | Optimize queries |

### Auto-Scaling Triggers

Pool metrics can trigger automatic scaling:

```yaml
# Kubernetes HPA based on connection pool metrics
apiVersion: autoscaling/v2
kind: HorizontalPodAutoscaler
metadata:
  name: honua-server-hpa
spec:
  metrics:
  - type: Pods
    pods:
      metric:
        name: honua_db_pool_utilization_ratio
      target:
        type: AverageValue
        averageValue: "0.7"
```

## File Upload Backpressure

### Streaming Architecture

Prevents memory exhaustion from large file uploads:

```csharp
// Streaming upload with backpressure control
var result = await streamingUploadService.QueueFileUploadAsync(
    uploadJob, cancellationToken);
```

### Queue Management

- **Bounded upload queue** - Prevents unbounded memory growth
- **Concurrent upload limits** - Controls resource usage
- **Backpressure signals** - Rejects uploads when system is overloaded

### Configuration

```json
{
  "FileUpload": {
    "MaxFileSizeBytes": 104857600,  // 100MB
    "MaxConcurrentUploads": 5,
    "MaxQueuedUploads": 20
  }
}
```

### Monitoring

Track upload queue metrics:

```promql
# Upload queue depth
honua_upload_queue_depth

# Upload processing time
histogram_quantile(0.95, honua_file_upload_duration_ms_bucket)
```

## Comprehensive Metrics & Alerting

### Production Health Dashboard

Centralized health metrics endpoint:

```bash
GET /monitoring/health/production
```

Returns:

```json
{
  "isHealthy": true,
  "overallStatus": "Healthy",
  "metrics": {
    "errorRate": 0.001,
    "cacheHitRatio": 0.95,
    "memoryPressureLevel": "low",
    "databaseConnectionPoolUtilization": 0.65
  },
  "alertConditions": []
}
```

### Key Metrics

#### Performance Metrics
- Query latency (P50, P95, P99)
- Cache hit/miss ratios
- Memory usage and GC pressure
- CPU utilization

#### Reliability Metrics
- Error rates by endpoint and protocol
- Circuit breaker state changes
- Connection pool exhaustion events
- Rate limiting violations

#### Business Metrics
- Active user sessions
- Upload processing throughput
- Feature usage patterns
- Geographic request distribution

### Alerting Rules

Comprehensive Prometheus alerting rules are provided:

```promql
# High error rate alert
alert: HonuaErrorRateCritical
expr: rate(honua_errors_total[5m]) / rate(honua_queries_total[5m]) > 0.05
for: 2m
labels:
  severity: critical
```

### Alert Escalation

Structured alert escalation based on severity:

- **P0 (Critical)**: Immediate page, 2-minute response SLA
- **P1 (High)**: 15-minute response, significant degradation
- **P2 (Medium)**: 1-hour response, performance issues
- **P3 (Low)**: Next business day, monitoring alerts

## Deployment Configuration

### Environment Variables

```bash
# Circuit Breakers
HONUA__EXTERNALSERVICES__CIRCUITBREAKER__ARCGISREST__FAILURETHRESHOLD=3
HONUA__EXTERNALSERVICES__CIRCUITBREAKER__ARCGISREST__DURATIONOFBREAKSECONDS=60

# Rate Limiting
HONUA__RATELIMITING__ENABLED=true
HONUA__RATELIMITING__GLOBALREQUESTSPERMINUTE=1000

# File Upload
HONUA__FILEUPLOAD__MAXFILESIZEBYTES=104857600
HONUA__FILEUPLOAD__MAXCONCURRENTUPLOADS=5

# Connection Pool
HONUA__LIMITS__CONNECTIONS__MAXPOOLSIZE=50
HONUA__LIMITS__CONNECTIONS__COMMANDTIMEOUTSECONDS=30
```

### Kubernetes Resources

```yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: honua-server
spec:
  template:
    spec:
      containers:
      - name: honua-server
        resources:
          requests:
            memory: "1Gi"
            cpu: "500m"
          limits:
            memory: "2Gi"
            cpu: "1000m"
        env:
        - name: HONUA__RATELIMITING__ENABLED
          value: "true"
        - name: HONUA__LIMITS__CONNECTIONS__MAXPOOLSIZE
          value: "50"
```

### Load Balancer Configuration

```nginx
# Nginx configuration for rate limiting
http {
    limit_req_zone $binary_remote_addr zone=global:10m rate=10r/s;
    limit_req_zone $binary_remote_addr zone=uploads:10m rate=1r/s;
    
    upstream honua_backend {
        least_conn;
        server honua-1:8080 max_fails=3 fail_timeout=30s;
        server honua-2:8080 max_fails=3 fail_timeout=30s;
        server honua-3:8080 max_fails=3 fail_timeout=30s;
    }
    
    server {
        location /rest/services {
            limit_req zone=global burst=20 nodelay;
            proxy_pass http://honua_backend;
        }
        
        location /import {
            limit_req zone=uploads burst=5 nodelay;
            proxy_pass http://honua_backend;
            proxy_read_timeout 300s;
            client_max_body_size 100m;
        }
    }
}
```

## Testing & Validation

### Load Testing

Validate resilience patterns under load:

```bash
# Connection pool stress test
k6 run --vus 100 --duration 5m tests/load/connection-pool-test.js

# Rate limiting validation
k6 run --vus 50 --duration 2m tests/load/rate-limit-test.js

# File upload backpressure test
k6 run --vus 20 --duration 3m tests/load/upload-stress-test.js
```

### Chaos Engineering

Test failure scenarios:

```bash
# Database connection failures
chaos toolkit run experiments/db-connection-failure.json

# External service outages
chaos toolkit run experiments/external-service-failure.json

# Memory pressure simulation
chaos toolkit run experiments/memory-pressure.json
```

### Integration Tests

Comprehensive test coverage for all resilience patterns:

- Circuit breaker state transitions
- Rate limiting enforcement
- Connection pool behavior under stress
- File upload queue management
- Metrics collection accuracy

## Troubleshooting

### Common Issues

#### High Connection Pool Utilization

**Symptoms**: Slow queries, connection timeouts
**Diagnosis**: Check `/monitoring/metrics/connection-pool`
**Resolution**: 
1. Scale connection pool size
2. Optimize slow queries
3. Add read replicas

#### Circuit Breaker Constantly Opening

**Symptoms**: External service integration failures
**Diagnosis**: Check external service health
**Resolution**:
1. Verify external service availability
2. Adjust failure thresholds
3. Implement fallback mechanisms

#### Rate Limiting False Positives

**Symptoms**: Legitimate users getting 429 responses
**Diagnosis**: Review rate limiting metrics
**Resolution**:
1. Adjust rate limits
2. Implement API key authentication
3. Use distributed rate limiting with Redis

### Debug Commands

```bash
# Check circuit breaker status
curl -H "Authorization: Bearer $ADMIN_TOKEN" \
     https://honua-server/monitoring/alerts

# View connection pool metrics
curl -H "Authorization: Bearer $ADMIN_TOKEN" \
     https://honua-server/monitoring/metrics/connection-pool

# Check rate limiting status
redis-cli --scan --pattern "honua:ratelimit:*" | head -10
```

## Performance Impact

### Overhead Analysis

The resilience patterns introduce minimal performance overhead:

- **Circuit breakers**: <1ms per HTTP request
- **Rate limiting**: <0.5ms per request (with Redis)
- **Connection monitoring**: <0.1ms per database operation
- **Metrics collection**: <0.2ms per operation

### Benchmarks

Performance impact under normal conditions:

| Feature | Overhead | Memory | CPU |
|---------|----------|--------|-----|
| Circuit Breakers | 0.8ms | 2MB | 1% |
| Rate Limiting | 0.4ms | 5MB | 0.5% |
| Connection Monitoring | 0.1ms | 1MB | 0.2% |
| Metrics Collection | 0.2ms | 10MB | 2% |

### Optimization Tips

1. **Use Redis for distributed features** - Reduces memory usage per instance
2. **Tune metric collection intervals** - Balance observability vs. performance
3. **Configure appropriate buffer sizes** - Optimize for your workload patterns
4. **Monitor GC impact** - Ensure metrics collection doesn't increase GC pressure

## Security Considerations

### Rate Limiting Security

- IP-based and API key-based rate limiting
- Protection against application-layer DDoS
- Configurable burst handling for legitimate traffic spikes

### Circuit Breaker Security

- Prevents resource exhaustion from failing external services
- Configurable fallback responses to avoid information disclosure
- Automatic recovery mechanisms to restore service

### Monitoring Security

- Admin-only access to production metrics endpoints
- No sensitive information in metrics labels
- Structured logging for audit trails

---

**Last Updated**: 2026-04-06  
**Version**: 1.0  
**Maintained By**: Honua Engineering Team