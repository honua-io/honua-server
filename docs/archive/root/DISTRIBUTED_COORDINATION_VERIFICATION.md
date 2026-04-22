# Distributed Coordination Verification Guide

This guide demonstrates how the distributed cache coordination implementation enables horizontal scaling.

## Problem Solved

**Before**: Cache refresh coordination used local `ConcurrentDictionary`, preventing horizontal scaling:
```csharp
// OLD: Local-only state (src/Honua.Server/Features/Infrastructure/Caching/CacheRefreshCoordinator.cs:32)
private readonly ConcurrentDictionary<string, byte> _pendingKeys = new(StringComparer.Ordinal);
```

**After**: Redis-based distributed coordination enables multi-instance deployments:
```csharp
// NEW: Redis-based distributed state management
var acquired = _redisDb.StringSet(redisKey, _instanceId, RedisLockExpiry, When.NotExists);
```

## Key Implementation Features

### 1. Distributed Cache Deduplication
Multiple instances coordinate cache refreshes via Redis:

```csharp
// Atomic distributed lock acquisition
private bool TryClaimDistributedRefresh(string key)
{
    var redisKey = RedisKeyPrefix + "pending:" + key;
    return _redisDb.StringSet(redisKey, _instanceId, RedisLockExpiry, When.NotExists);
}
```

### 2. Cross-Instance Invalidation
Cache invalidations propagate across all instances:

```csharp
// Cluster-wide invalidation via Redis pub/sub
public async Task NotifyInvalidationClusterWideAsync(string key, CancellationToken ct = default)
{
    await _redisSubscriber.PublishAsync(RedisInvalidationChannel, key);
}
```

### 3. Leader Election for Background Services
Only one instance performs expensive operations:

```csharp
// CRS warmup service with leader election
if (isLeader)
{
    await PerformWarmupAsync(stoppingToken);
    // Continue warming up periodically while leader
}
```

## Verification Steps

### 1. Multi-Instance Cache Coordination

**Test Scenario**: Deploy two instances, verify cache refresh deduplication
```csharp
// Instance 1 and Instance 2 both try to refresh "layer:expensive-data"
// Result: Only one instance performs the refresh (Redis coordination)
coordinator1.TryEnqueueRefresh("layer:expensive-data", RefreshCallback);
coordinator2.TryEnqueueRefresh("layer:expensive-data", RefreshCallback);
// With Redis: Only one executes, second returns false (deduplicated)
// Without Redis: Both execute independently (fallback mode)
```

**Code Location**: `tests/dotnet/Honua.Server.Tests/Features/Integration/DistributedCoordinationIntegrationTests.cs`

### 2. Leader Election for Background Tasks

**Test Scenario**: Deploy multiple instances, verify only one performs CRS warmup
```csharp
// Multiple instances start up
PostgresCrsWarmupService service1 = new(..., leaderElection1);
PostgresCrsWarmupService service2 = new(..., leaderElection2);

// Only one becomes leader and performs warmup
bool leader1 = await leaderElection1.TryAcquireLeadershipAsync();
bool leader2 = await leaderElection2.TryAcquireLeadershipAsync();
// With Redis: leader1 = true, leader2 = false
// Without Redis: leader1 = true, leader2 = true (fallback)
```

### 3. Redis Failure Resilience

**Test Scenario**: Redis goes down, verify graceful fallback
```csharp
// Redis connection fails
redis.IsConnected = false;

// Coordination continues in local mode
var canRefresh = coordinator.TryEnqueueRefresh("test", callback);
Assert.True(canRefresh); // Still works, just locally
Assert.False(coordinator.IsDistributed); // Knows it's in fallback mode
```

## Configuration

### Enable Distributed Mode
Ensure Redis is configured in `appsettings.json`:
```json
{
  "ConnectionStrings": {
    "redis": "localhost:6379"
  }
}
```

### Monitor Coordination
Check logs for coordination activities:
```
INFO: Distributed cache coordination enabled
INFO: Leadership acquired for honua:leader:crs-warmup by instance ServerA_a1b2c3d4
INFO: Background cache refresh started with max 4 concurrent operations (distributed: true)
```

## Redis Key Usage

### Cache Coordination Keys
- `honua:cache:refresh:pending:{cache-key}` - Pending refresh locks
- `honua:cache:refresh:invalidated:{cache-key}` - Invalidation flags

### Leader Election Keys  
- `honua:leader:crs-warmup` - CRS warmup service leadership
- `honua:leader:{service-name}` - General pattern for other services

### Pub/Sub Channels
- `honua:cache:invalidations` - Cross-instance invalidation messages

## Performance Impact

### Redis Operations
- **Cache Coordination**: 1-2 Redis operations per cache refresh
- **Leader Election**: 1 Redis operation every 60 seconds (heartbeat)
- **Invalidations**: 1 Redis publish per cluster-wide invalidation

### Fallback Performance
- **No Redis**: Zero performance impact, identical to original behavior
- **Redis Failures**: Temporary fallback, no service interruption

## Deployment Strategy

### Zero-Downtime Deployment
1. Deploy new version with distributed coordination (backward compatible)
2. Old instances use local coordination, new instances use Redis
3. Gradually replace instances - no coordination conflicts
4. Redis coordination fully active once all instances upgraded

### Scaling Verification
```bash
# Deploy multiple instances
kubectl scale deployment honua-server --replicas=3

# Verify only one performs CRS warmup (check logs)
kubectl logs -l app=honua-server | grep "CRS warmup"
# Should see leadership acquired by only one instance

# Verify cache coordination
# Monitor Redis keys for coordination activity
redis-cli keys "honua:cache:refresh:*"
```

## Testing the Implementation

Run the distributed coordination tests:
```bash
# Unit tests for individual components
dotnet test --filter "DistributedCacheRefreshCoordinatorTests"
dotnet test --filter "RedisDistributedLeaderElectionTests"

# Integration tests for multi-instance scenarios  
dotnet test --filter "DistributedCoordinationIntegrationTests"
```

## Success Criteria Met ✅

1. **Multiple instances coordinate without duplication** - Redis-based deduplication
2. **Background services elect single leader** - Leader election for expensive operations
3. **Graceful fallback when Redis unavailable** - Local coordination mode
4. **Production-ready error handling** - Comprehensive exception handling
5. **Zero breaking changes** - Backward compatible implementation

The implementation successfully enables horizontal scaling while maintaining robustness and operational simplicity.