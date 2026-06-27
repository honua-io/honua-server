// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Admin;

/// <summary>
/// Source-generated logger for Admin features (AOT compatible)
/// </summary>
internal static partial class AdminLog
{
    /// <summary>
    /// Log when connection not found for table discovery
    /// </summary>
    [LoggerMessage(
        EventId = 4001,
        Level = LogLevel.Warning,
        Message = "No connection string found for connection ID {ConnectionId}")]
    public static partial void ConnectionNotFound(ILogger logger, string connectionId);

    /// <summary>
    /// Log successful table discovery
    /// </summary>
    [LoggerMessage(
        EventId = 4002,
        Level = LogLevel.Information,
        Message = "Successfully discovered {Count} tables for connection {ConnectionId}")]
    public static partial void TableDiscoverySuccessful(ILogger logger, int count, string connectionId);

    /// <summary>
    /// Log table discovery failure
    /// </summary>
    [LoggerMessage(
        EventId = 4003,
        Level = LogLevel.Error,
        Message = "Failed to discover tables for connection {ConnectionId}")]
    public static partial void TableDiscoveryFailed(ILogger logger, Exception ex, string connectionId);

    /// <summary>
    /// Log PostGIS table discovery completion
    /// </summary>
    [LoggerMessage(
        EventId = 4004,
        Level = LogLevel.Information,
        Message = "Discovered {Count} PostGIS tables")]
    public static partial void PostGisTablesDiscovered(ILogger logger, int count);

    /// <summary>
    /// Log PostGIS table discovery error
    /// </summary>
    [LoggerMessage(
        EventId = 4005,
        Level = LogLevel.Error,
        Message = "Error discovering PostGIS tables")]
    public static partial void PostGisDiscoveryError(ILogger logger, Exception ex);

    /// <summary>
    /// Log when license status is queried.
    /// </summary>
    [LoggerMessage(
        EventId = 4070,
        Level = LogLevel.Debug,
        Message = "License status queried")]
    public static partial void LicenseStatusQueried(ILogger logger);

    /// <summary>
    /// Log when a license upload is attempted.
    /// </summary>
    [LoggerMessage(
        EventId = 4071,
        Level = LogLevel.Information,
        Message = "License upload attempted")]
    public static partial void LicenseUploadAttempted(ILogger logger);

    /// <summary>
    /// Log when cache invalidation is requested.
    /// </summary>
    [LoggerMessage(
        EventId = 4072,
        Level = LogLevel.Information,
        Message = "Cache invalidation requested for scope {Scope} by user {User}")]
    public static partial void CacheInvalidationRequested(ILogger logger, string scope, string user);

    /// <summary>
    /// Log when cache invalidation completes.
    /// </summary>
    [LoggerMessage(
        EventId = 4073,
        Level = LogLevel.Information,
        Message = "Cache invalidation completed for scope {Scope}")]
    public static partial void CacheInvalidationCompleted(ILogger logger, string scope);

    /// <summary>
    /// Log when cache invalidation fails.
    /// </summary>
    [LoggerMessage(
        EventId = 4074,
        Level = LogLevel.Error,
        Message = "Cache invalidation failed for scope {Scope}")]
    public static partial void CacheInvalidationFailed(ILogger logger, Exception ex, string scope);

    /// <summary>
    /// Log when an identity provider connectivity test is started.
    /// </summary>
    [LoggerMessage(
        EventId = 4075,
        Level = LogLevel.Debug,
        Message = "Identity provider connectivity test started for {ProviderType}")]
    public static partial void IdentityProviderTestStarted(ILogger logger, string providerType);

    /// <summary>
    /// Log when an identity provider connectivity test completes.
    /// </summary>
    [LoggerMessage(
        EventId = 4076,
        Level = LogLevel.Information,
        Message = "Identity provider connectivity test completed for {ProviderType}, reachable: {IsReachable}")]
    public static partial void IdentityProviderTestCompleted(ILogger logger, string providerType, bool isReachable);

    /// <summary>
    /// Log when geocoding provider health is queried.
    /// </summary>
    [LoggerMessage(
        EventId = 4077,
        Level = LogLevel.Debug,
        Message = "Geocoding provider health queried")]
    public static partial void GeocodingProviderHealthQueried(ILogger logger);

    /// <summary>
    /// Log when feature overview is queried.
    /// </summary>
    [LoggerMessage(
        EventId = 4078,
        Level = LogLevel.Debug,
        Message = "Feature overview queried")]
    public static partial void FeatureOverviewQueried(ILogger logger);

    /// <summary>
    /// Log when license entitlements are queried.
    /// </summary>
    [LoggerMessage(
        EventId = 4079,
        Level = LogLevel.Debug,
        Message = "License entitlements queried")]
    public static partial void LicenseEntitlementsQueried(ILogger logger);

    /// <summary>
    /// Log when identity providers configuration is queried.
    /// </summary>
    [LoggerMessage(
        EventId = 4080,
        Level = LogLevel.Debug,
        Message = "Identity providers queried")]
    public static partial void IdentityProvidersQueried(ILogger logger);

    /// <summary>
    /// Log when cache status is queried.
    /// </summary>
    [LoggerMessage(
        EventId = 4081,
        Level = LogLevel.Debug,
        Message = "Cache status queried")]
    public static partial void CacheStatusQueried(ILogger logger);

    // Cache operations (4600-4609)

    /// <summary>
    /// Log cache health check result
    /// </summary>
    [LoggerMessage(
        EventId = 4600,
        Level = LogLevel.Information,
        Message = "Cache health check completed: healthy={IsHealthy}, fallback={IsUsingFallback}")]
    public static partial void CacheHealthCheckCompleted(ILogger logger, bool isHealthy, bool isUsingFallback);

    /// <summary>
    /// Log cache invalidation request
    /// </summary>
    [LoggerMessage(
        EventId = 4601,
        Level = LogLevel.Information,
        Message = "Cache invalidation requested: scope={Scope}")]
    public static partial void CacheInvalidationRequested(ILogger logger, string scope);

    /// <summary>
    /// Log cache operations invalidation failure
    /// </summary>
    [LoggerMessage(
        EventId = 4602,
        Level = LogLevel.Warning,
        Message = "Cache invalidation failed: scope={Scope}")]
    public static partial void CacheOperationInvalidationFailed(ILogger logger, Exception ex, string scope);

    /// <summary>
    /// Log cache health check failure
    /// </summary>
    [LoggerMessage(
        EventId = 4603,
        Level = LogLevel.Warning,
        Message = "Cache health check failed")]
    public static partial void CacheHealthCheckFailed(ILogger logger, Exception ex);

    /// <summary>
    /// Log Redis server info retrieval failure
    /// </summary>
    [LoggerMessage(
        EventId = 4604,
        Level = LogLevel.Warning,
        Message = "Failed to retrieve Redis server info for cache health response")]
    public static partial void RedisInfoRetrievalFailed(ILogger logger, Exception ex);

    // Streaming operations (4610-4619)

    /// <summary>
    /// Log streaming subscriber list request
    /// </summary>
    [LoggerMessage(
        EventId = 4610,
        Level = LogLevel.Information,
        Message = "Streaming subscribers listed: count={Count}")]
    public static partial void StreamingSubscribersListed(ILogger logger, int count);

    /// <summary>
    /// Log subscriber force-disconnect
    /// </summary>
    [LoggerMessage(
        EventId = 4611,
        Level = LogLevel.Information,
        Message = "Subscriber {SubscriberId} force-disconnected")]
    public static partial void SubscriberDisconnected(ILogger logger, Guid subscriberId);

    /// <summary>
    /// Log subscriber not found for disconnect
    /// </summary>
    [LoggerMessage(
        EventId = 4612,
        Level = LogLevel.Warning,
        Message = "Subscriber {SubscriberId} not found for disconnect")]
    public static partial void SubscriberNotFound(ILogger logger, Guid subscriberId);

    // Geocoding operations (4620-4629)

    /// <summary>
    /// Log geocoding provider health check
    /// </summary>
    [LoggerMessage(
        EventId = 4620,
        Level = LogLevel.Information,
        Message = "Geocoding provider health checked: {ProviderCount} providers")]
    public static partial void GeocodingProviderHealthChecked(ILogger logger, int providerCount);

    /// <summary>
    /// Log geocoding provider health check failure
    /// </summary>
    [LoggerMessage(
        EventId = 4621,
        Level = LogLevel.Warning,
        Message = "Geocoding provider health check failed")]
    public static partial void GeocodingProviderHealthCheckFailed(ILogger logger, Exception ex);
}
