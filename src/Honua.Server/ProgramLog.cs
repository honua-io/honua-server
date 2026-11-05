// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

internal static partial class ProgramLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Redis multiplexer initialized without an active connection. The client will continue retrying in the background.")]
    public static partial void RedisStartupConnectionInactive(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to connect to Redis at startup. RedisCacheService will operate in fallback mode.")]
    public static partial void RedisStartupConnectionFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Admin API keys are held in process memory on a multi-instance host without Redis or a PostgreSQL database; a key minted on one instance is unknown to the others.")]
    public static partial void AdminApiKeysPerInstance(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Redis durability attestation: attested ({PersistenceMode}, {AcknowledgedWritePolicy}, maxmemory-policy={EvictionPolicy}). Published as limits.job.redisDurability on the capability manifest.")]
    public static partial void RedisDurabilityAttested(
        ILogger logger,
        string persistenceMode,
        string acknowledgedWritePolicy,
        string evictionPolicy);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Redis durability attestation: {Status} (the persistence policy could not be read: {Detail}). {Consequence} {Guidance} Jobs:RequireDurableStore=true would refuse startup on this Redis.")]
    public static partial void RedisDurabilityUnverified(
        ILogger logger,
        string status,
        string detail,
        string consequence,
        string guidance);

    // A non-durable Redis policy is a warning and is never a startup rejection.
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Redis durability attestation: {Status} ({Cause}: {Detail}). {Consequence} To make acknowledged writes durable: {Guidance} Jobs:RequireDurableStore=true would refuse startup on this Redis.")]
    public static partial void RedisDurabilityNotDurable(
        ILogger logger,
        string status,
        Honua.Core.Features.Capabilities.DurableJobSubstrateCause cause,
        string detail,
        string consequence,
        string guidance);
}
