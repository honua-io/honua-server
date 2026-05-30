// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Redis;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Honua.Infrastructure.Redis;

/// <summary>
/// Health check for Redis connectivity and standardized Redis services.
/// </summary>
internal sealed class RedisHealthCheck : IHealthCheck
{
    private readonly IRedisHealthMonitor _healthMonitor;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<RedisHealthCheck> _logger;

    public RedisHealthCheck(
        IRedisHealthMonitor healthMonitor,
        IServiceProvider serviceProvider,
        ILogger<RedisHealthCheck> logger)
    {
        _healthMonitor = healthMonitor;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var data = new Dictionary<string, object>
            {
                ["redis_available"] = _healthMonitor.IsRedisAvailable,
                ["redis_ever_available"] = _healthMonitor.WasRedisEverAvailable,
                ["consecutive_failures"] = _healthMonitor.ConsecutiveFailures,
                ["should_retry"] = _healthMonitor.ShouldRetryRedis
            };

            if (_healthMonitor.LastSuccessfulContact.HasValue)
            {
                data["last_successful_contact"] = _healthMonitor.LastSuccessfulContact.Value;
            }

            if (_healthMonitor.LastFailure.HasValue)
            {
                data["last_failure"] = _healthMonitor.LastFailure.Value;
            }

            // Test connectivity
            var isConnected = await _healthMonitor.TestConnectivityAsync(cancellationToken);
            data["connectivity_test"] = isConnected;

            // Check Redis services status
            CheckRedisServices(data);

            if (!_healthMonitor.WasRedisEverAvailable)
            {
                return HealthCheckResult.Healthy("Redis not configured", data);
            }

            if (!isConnected)
            {
                var failureMessage = _healthMonitor.ConsecutiveFailures > 1
                    ? $"Redis unavailable ({_healthMonitor.ConsecutiveFailures} consecutive failures)"
                    : "Redis unavailable";

                return HealthCheckResult.Unhealthy(failureMessage, data: data);
            }

            return HealthCheckResult.Healthy("Redis available", data);
        }
        catch (Exception ex)
        {
            RedisHealthCheckLog.HealthCheckFailed(_logger, ex);
            return HealthCheckResult.Unhealthy("Redis health check failed", ex);
        }
    }

    private void CheckRedisServices(Dictionary<string, object> data)
    {
        try
        {
            // Check leader election services
            var leaderElections = _serviceProvider.GetServices<IRedisLeaderElection>().ToArray();
            if (leaderElections.Length > 0)
            {
                data["leader_elections"] = leaderElections.Select(le => new
                {
                    key = le.LeadershipKey,
                    is_leader = le.IsLeader,
                    node_id = le.NodeId,
                    configured = le.IsRedisConfigured
                }).ToArray();
            }

            // Check job queue services
            var jobQueues = _serviceProvider.GetServices<IRedisJobQueue>().ToArray();
            if (jobQueues.Length > 0)
            {
                data["job_queues"] = jobQueues.Select(jq => new
                {
                    key = jq.QueueKey,
                    using_redis = jq.IsUsingRedis,
                    fallback_mode = jq is RedisServiceBase serviceBase ? serviceBase.FallbackMode.ToString() : "Unknown",
                    fallback_queue_length = jq is RedisJobQueue redisJobQueue ? redisJobQueue.FallbackQueueLength : 0,
                    in_flight_count = jq is RedisJobQueue redisJobQueue2 ? redisJobQueue2.InFlightCount : 0
                }).ToArray();
            }

            // Check if multiple job queues are registered
            var namedJobQueues = _serviceProvider.GetService<IReadOnlyDictionary<string, IRedisJobQueue>>();
            if (namedJobQueues?.Count > 0)
            {
                data["named_job_queues"] = namedJobQueues.Select(kvp => new
                {
                    name = kvp.Key,
                    key = kvp.Value.QueueKey,
                    using_redis = kvp.Value.IsUsingRedis,
                    fallback_mode = kvp.Value is RedisServiceBase serviceBase ? serviceBase.FallbackMode.ToString() : "Unknown"
                }).ToArray();
            }
        }
        catch (Exception ex)
        {
            data["service_check_error"] = ex.Message;
        }
    }
}
