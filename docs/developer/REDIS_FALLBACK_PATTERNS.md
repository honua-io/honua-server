# Redis Fallback Patterns

This document describes the standardized Redis fallback behaviors implemented in Honua Server to ensure consistent behavior across all Redis-dependent services and prevent split-brain scenarios in multi-node deployments.

## Overview

The Redis fallback infrastructure provides consistent, configurable fallback strategies for all Redis-dependent services, ensuring:

- **Consistent behavior** across different services
- **Split-brain prevention** in multi-node deployments
- **Graceful degradation** when appropriate
- **Circuit breaker patterns** for resilience
- **Comprehensive monitoring** and health checks

## Core Components

### IRedisHealthMonitor

Centralized Redis health monitoring with circuit breaker functionality:

```csharp
public interface IRedisHealthMonitor
{
    bool IsRedisAvailable { get; }
    bool WasRedisEverAvailable { get; }
    DateTimeOffset? LastSuccessfulContact { get; }
    int ConsecutiveFailures { get; }
    bool ShouldRetryRedis { get; }
    
    void RecordSuccess();
    void RecordFailure(Exception exception);
    Task<bool> TestConnectivityAsync(CancellationToken cancellationToken = default);
}
```

### Fallback Strategies

Three standardized fallback strategies are available:

#### 1. FailFast
- **Use case**: Critical distributed coordination where consistency is essential
- **Behavior**: Operations fail immediately when Redis is unavailable
- **Example**: Leader election in production environments

```csharp
services.AddRedisLeaderElection("critical-coordination", fallbackStrategy: RedisFallbackMode.FailFast);
```

#### 2. InMemoryFallback
- **Use case**: Caching and non-critical operations where availability is more important than consistency
- **Behavior**: Falls back to in-memory operations when Redis is unavailable
- **Example**: Application caches, temporary data storage

```csharp
services.AddRedisJobQueue("background-tasks", RedisFallbackMode.InMemoryFallback);
```

#### 3. AllowLocalInDev
- **Use case**: Services that require distributed coordination in production but can operate locally in dev/test
- **Behavior**: Allows fallback in development/test environments, fails fast in production
<<<<<<< HEAD
- **Example**: Import job coordination, workflow orchestration

```csharp
services.AddRedisLeaderElection("workflow-coordination", fallbackStrategy: RedisFallbackMode.AllowLocalInDev);
```

=======
- **Example**: Import job coordination

```csharp
services.AddRedisLeaderElection("import-coordination", fallbackStrategy: RedisFallbackMode.AllowLocalInDev);
```

#### Conditional Registration (no fallback)

Some features require Redis unconditionally and skip registration entirely when
`IConnectionMultiplexer` is absent. This avoids DI activation failures while
producing a clear operational signal (e.g. `503` on affected admin endpoints).

- **Use case**: Durable stores and background services that have no meaningful
  in-memory or local alternative
- **Behavior**: Services are not registered; dependent features are unavailable
- **Example**: Workflow orchestration (`AddOrchestration` / `AddOrchestrationBackgroundServices`)

>>>>>>> origin/trunk
### RedisServiceBase

Base class for all Redis-dependent services providing:

```csharp
public abstract class RedisServiceBase : IRedisService
{
    protected async Task<T> ExecuteWithFallbackAsync<T>(
        string operation,
        Func<IDatabase, CancellationToken, Task<T>> redisOperation,
        Func<CancellationToken, Task<T>>? fallbackOperation = null,
        CancellationToken cancellationToken = default);
}
```

## Service Implementations

### Redis Leader Election

Distributed leader election with automatic lease renewal:

```csharp
public interface IRedisLeaderElection : IDisposable
{
    string NodeId { get; }
    bool IsLeader { get; }
    bool IsConfigured { get; }
    TimeSpan LeaseDuration { get; }
    
    Task<bool> TryAcquireOrExtendLeadershipAsync(CancellationToken cancellationToken = default);
    Task ReleaseLeadershipAsync(CancellationToken cancellationToken = default);
    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    
    event EventHandler<LeadershipChangedEventArgs>? LeadershipChanged;
}
```

**Key features:**
- Automatic lease renewal (every 20 seconds for 60-second leases)
- Split-brain prevention in production environments
- Graceful fallback in development environments
- Leadership change events for coordination

### Redis Job Queue

Distributed job queue with in-memory fallback:

```csharp
public interface IRedisJobQueue : IRedisService
{
    string QueueKey { get; }
    int InFlightCount { get; }
    int FallbackQueueLength { get; }
    
    Task EnqueueAsync(string jobId, CancellationToken cancellationToken = default);
    Task<string?> DequeueAsync(TimeSpan timeout, CancellationToken cancellationToken = default);
    Task<long> GetQueueLengthAsync(CancellationToken cancellationToken = default);
    Task CompleteAsync(string jobId, CancellationToken cancellationToken = default);
    Task RecoverInFlightAsync(CancellationToken cancellationToken = default);
}
```

**Key features:**
- Bounded in-memory fallback queue (10,000 items max)
- Automatic Redis restoration attempts
- In-flight job tracking and recovery
- Consistent behavior across Redis and fallback modes

## Configuration

### Service Registration

```csharp
public void ConfigureServices(IServiceCollection services)
{
    // Add core Redis infrastructure
    services.AddStandardizedRedisInfrastructure();
    
    // Add specific services with appropriate strategies
    services.AddRedisLeaderElection(
        leadershipKey: "background-worker-leader",
        leaseDuration: TimeSpan.FromMinutes(1));
        
    services.AddRedisJobQueue(
        queueKey: "import-jobs",
        fallbackStrategy: RedisFallbackMode.AllowLocalInDev);
        
    // Add health checks
    services.AddRedisHealthCheck();
}
```

### Multiple Job Queues

```csharp
services.AddRedisJobQueues(new Dictionary<string, RedisFallbackMode>
{
    ["critical-operations"] = RedisFallbackMode.FailFast,
    ["background-tasks"] = RedisFallbackMode.InMemoryFallback,
    ["import-jobs"] = RedisFallbackMode.AllowLocalInDev
});
```

## Split-Brain Prevention

The standardized infrastructure prevents split-brain scenarios through:

### 1. Consistent Failure Detection
All services use the same `IRedisHealthMonitor` for failure detection:
- Circuit breaker pattern with 30-second retry intervals
- Consecutive failure tracking
- Standardized Redis exception handling

### 2. Environment-Aware Fallback
Services automatically adjust behavior based on environment:

```csharp
// Production: Redis failure means no service can claim leadership
if (hostEnvironment.IsProduction() && !redis.IsAvailable)
{
    return false; // Fail fast
}

// Development: Allow local coordination
if (hostEnvironment.IsDevelopment())
{
    return true; // Allow fallback
}
```

### 3. Coordinated Recovery
When Redis comes back online:
- All services detect restoration simultaneously
- Leadership re-election occurs through Redis coordination
- Fallback data is preserved during transition

## Health Monitoring

The Redis health check provides comprehensive status information:

```json
{
  "status": "Healthy|Unhealthy",
  "data": {
    "redis_available": true,
    "redis_ever_available": true,
    "consecutive_failures": 0,
    "last_successful_contact": "2026-04-12T10:30:00Z",
    "leader_elections": [
      {
        "key": "workflow-leader",
        "is_leader": true,
        "node_id": "server1:1234:abc123",
        "configured": true
      }
    ],
    "job_queues": [
      {
        "key": "import-queue",
        "using_redis": true,
        "fallback_mode": "AllowLocalInDev",
        "fallback_queue_length": 0,
        "in_flight_count": 2
      }
    ]
  }
}
```

## Best Practices

### 1. Choose Appropriate Fallback Strategy
- Use `FailFast` for critical coordination (leader election, distributed locks)
- Use `InMemoryFallback` for caching and temporary data
- Use `AllowLocalInDev` for business logic that needs coordination in production

### 2. Handle Leadership Changes
```csharp
leaderElection.LeadershipChanged += (sender, args) =>
{
    if (args.IsLeader)
    {
        // Start leader-only operations
        await backgroundService.StartAsync();
    }
    else
    {
        // Stop leader-only operations
        await backgroundService.StopAsync();
    }
};
```

### 3. Monitor Fallback Usage
- Set up alerts for fallback queue growth
- Monitor Redis connectivity metrics
- Track split-brain prevention events

### 4. Test Failure Scenarios
```csharp
[IntegrationTest]
public async Task VerifyNoSplitBrain_WhenRedisFailsInProduction()
{
    // Create multiple service instances
    // Simulate Redis failure
    // Verify no multiple leaders exist
    // Verify consistent fallback behavior
}
```

## Migration Guide

### Updating Existing Services

1. Replace direct Redis usage with standardized services:

```csharp
// Old: Direct Redis usage
var redis = serviceProvider.GetService<IConnectionMultiplexer>();
var database = redis?.GetDatabase();

// New: Standardized service
var leaderElection = serviceProvider.GetRequiredService<IRedisLeaderElection>();
```

2. Update service registration:

```csharp
// Old: Manual service creation
services.AddSingleton<MyService>(sp => new MyService(
    sp.GetService<IConnectionMultiplexer>(),
    sp.GetRequiredService<ILogger<MyService>>()));

// New: Standardized registration
services.AddRedisLeaderElection("my-service-leader");
```

3. Implement consistent error handling:

```csharp
// Services automatically handle Redis failures based on strategy
// No need for custom retry logic or fallback implementation
```

## Troubleshooting

### Common Issues

1. **Split-brain in production**: Check that services use `FailFast` or `AllowLocalInDev` strategies
2. **Memory leaks in fallback mode**: Verify fallback queue limits are enforced
3. **Inconsistent behavior**: Ensure all services use the same `IRedisHealthMonitor`
4. **Recovery issues**: Check Redis connectivity and circuit breaker state

### Debugging Tools

```csharp
// Check Redis health status
var healthMonitor = serviceProvider.GetRequiredService<IRedisHealthMonitor>();
logger.LogInformation("Redis available: {Available}, Failures: {Failures}", 
    healthMonitor.IsRedisAvailable, healthMonitor.ConsecutiveFailures);

// Check service fallback status
var jobQueue = serviceProvider.GetRequiredService<IRedisJobQueue>();
logger.LogInformation("Using Redis: {UsingRedis}, Fallback length: {FallbackLength}",
    jobQueue.IsUsingRedis, jobQueue.FallbackQueueLength);
```

This standardized approach ensures consistent, predictable behavior across all Redis-dependent services while maintaining high availability and preventing split-brain scenarios in distributed deployments.