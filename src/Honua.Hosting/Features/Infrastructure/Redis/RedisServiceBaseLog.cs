// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Redis;

namespace Honua.Infrastructure.Redis;

internal static partial class RedisServiceBaseLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Service {ServiceType} initialized with Redis support (strategy: {Strategy})")]
    public static partial void InitializedWithRedis(ILogger logger, string serviceType, RedisFallbackMode strategy);

    [LoggerMessage(Level = LogLevel.Information, Message = "Service {ServiceType} initialized without Redis (strategy: {Strategy})")]
    public static partial void InitializedWithoutRedis(ILogger logger, string serviceType, RedisFallbackMode strategy);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Redis unavailable for {Operation} - using fallback (service: {ServiceType})")]
    public static partial void UsingFallback(ILogger logger, string operation, string serviceType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Redis operation failed for {Operation} (service: {ServiceType})")]
    public static partial void OperationFailed(ILogger logger, string operation, string serviceType, Exception exception);
}
