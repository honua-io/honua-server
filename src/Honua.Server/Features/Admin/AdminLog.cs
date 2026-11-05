// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Features.Admin;

internal static partial class AdminLog
{
    [LoggerMessage(
        EventId = 4001,
        Level = LogLevel.Warning,
        Message = "No connection string found for connection ID {ConnectionId}")]
    public static partial void ConnectionNotFound(ILogger logger, string connectionId);

    [LoggerMessage(
        EventId = 4002,
        Level = LogLevel.Information,
        Message = "Successfully discovered {Count} tables for connection {ConnectionId}")]
    public static partial void TableDiscoverySuccessful(ILogger logger, int count, string connectionId);

    [LoggerMessage(
        EventId = 4003,
        Level = LogLevel.Error,
        Message = "Failed to discover tables for connection {ConnectionId}")]
    public static partial void TableDiscoveryFailed(ILogger logger, Exception ex, string connectionId);

    [LoggerMessage(
        EventId = 4004,
        Level = LogLevel.Information,
        Message = "Discovered {Count} PostGIS tables")]
    public static partial void PostGisTablesDiscovered(ILogger logger, int count);

    [LoggerMessage(
        EventId = 4005,
        Level = LogLevel.Error,
        Message = "Error discovering PostGIS tables")]
    public static partial void PostGisDiscoveryError(ILogger logger, Exception ex);

    [LoggerMessage(
        EventId = 4070,
        Level = LogLevel.Debug,
        Message = "License status queried")]
    public static partial void LicenseStatusQueried(ILogger logger);

    [LoggerMessage(
        EventId = 4071,
        Level = LogLevel.Information,
        Message = "License upload attempted")]
    public static partial void LicenseUploadAttempted(ILogger logger);

    [LoggerMessage(
        EventId = 4072,
        Level = LogLevel.Information,
        Message = "Cache invalidation requested for scope {Scope} by user {User}")]
    public static partial void CacheInvalidationRequested(ILogger logger, string scope, string user);

    [LoggerMessage(
        EventId = 4073,
        Level = LogLevel.Information,
        Message = "Cache invalidation completed for scope {Scope}")]
    public static partial void CacheInvalidationCompleted(ILogger logger, string scope);

    [LoggerMessage(
        EventId = 4074,
        Level = LogLevel.Error,
        Message = "Cache invalidation failed for scope {Scope}")]
    public static partial void CacheInvalidationFailed(ILogger logger, Exception ex, string scope);

    [LoggerMessage(
        EventId = 4075,
        Level = LogLevel.Debug,
        Message = "Identity provider connectivity test started for {ProviderType}")]
    public static partial void IdentityProviderTestStarted(ILogger logger, string providerType);

    [LoggerMessage(
        EventId = 4076,
        Level = LogLevel.Information,
        Message = "Identity provider connectivity test completed for {ProviderType}, reachable: {IsReachable}")]
    public static partial void IdentityProviderTestCompleted(ILogger logger, string providerType, bool isReachable);

    [LoggerMessage(
        EventId = 4077,
        Level = LogLevel.Debug,
        Message = "Geocoding provider health queried")]
    public static partial void GeocodingProviderHealthQueried(ILogger logger);

    [LoggerMessage(
        EventId = 4082,
        Level = LogLevel.Debug,
        Message = "Geoprocessing tool usage ranking queried, returned {Count} tools")]
    public static partial void GeoprocessingUsageRankingQueried(ILogger logger, int count);

    [LoggerMessage(
        EventId = 4078,
        Level = LogLevel.Debug,
        Message = "Feature overview queried")]
    public static partial void FeatureOverviewQueried(ILogger logger);

    [LoggerMessage(
        EventId = 4079,
        Level = LogLevel.Debug,
        Message = "License entitlements queried")]
    public static partial void LicenseEntitlementsQueried(ILogger logger);

    [LoggerMessage(
        EventId = 4080,
        Level = LogLevel.Debug,
        Message = "Identity providers queried")]
    public static partial void IdentityProvidersQueried(ILogger logger);

    [LoggerMessage(
        EventId = 4081,
        Level = LogLevel.Debug,
        Message = "Cache status queried")]
    public static partial void CacheStatusQueried(ILogger logger);

    // Cache operations (4600-4609)

    [LoggerMessage(
        EventId = 4600,
        Level = LogLevel.Information,
        Message = "Cache health check completed: healthy={IsHealthy}, fallback={IsUsingFallback}")]
    public static partial void CacheHealthCheckCompleted(ILogger logger, bool isHealthy, bool isUsingFallback);

    [LoggerMessage(
        EventId = 4601,
        Level = LogLevel.Information,
        Message = "Cache invalidation requested: scope={Scope}")]
    public static partial void CacheInvalidationRequested(ILogger logger, string scope);

    [LoggerMessage(
        EventId = 4602,
        Level = LogLevel.Warning,
        Message = "Cache invalidation failed: scope={Scope}")]
    public static partial void CacheOperationInvalidationFailed(ILogger logger, Exception ex, string scope);

    [LoggerMessage(
        EventId = 4603,
        Level = LogLevel.Warning,
        Message = "Cache health check failed")]
    public static partial void CacheHealthCheckFailed(ILogger logger, Exception ex);

    [LoggerMessage(
        EventId = 4604,
        Level = LogLevel.Warning,
        Message = "Failed to retrieve Redis server info for cache health response")]
    public static partial void RedisInfoRetrievalFailed(ILogger logger, Exception ex);

    // Streaming operations (4610-4619)

    [LoggerMessage(
        EventId = 4610,
        Level = LogLevel.Information,
        Message = "Streaming subscribers listed: count={Count}")]
    public static partial void StreamingSubscribersListed(ILogger logger, int count);

    [LoggerMessage(
        EventId = 4611,
        Level = LogLevel.Information,
        Message = "Subscriber {SubscriberId} force-disconnected")]
    public static partial void SubscriberDisconnected(ILogger logger, Guid subscriberId);

    [LoggerMessage(
        EventId = 4612,
        Level = LogLevel.Warning,
        Message = "Subscriber {SubscriberId} not found for disconnect")]
    public static partial void SubscriberNotFound(ILogger logger, Guid subscriberId);

    // Geocoding operations (4620-4629)

    [LoggerMessage(
        EventId = 4620,
        Level = LogLevel.Information,
        Message = "Geocoding provider health checked: {ProviderCount} providers")]
    public static partial void GeocodingProviderHealthChecked(ILogger logger, int providerCount);

    [LoggerMessage(
        EventId = 4621,
        Level = LogLevel.Warning,
        Message = "Geocoding provider health check failed")]
    public static partial void GeocodingProviderHealthCheckFailed(ILogger logger, Exception ex);

    [LoggerMessage(
        EventId = 4622,
        Level = LogLevel.Warning,
        Message = "Geocoder reference data import rejected: {Reason}")]
    public static partial void GeocoderReferenceImportRejected(ILogger logger, string reason);
}
