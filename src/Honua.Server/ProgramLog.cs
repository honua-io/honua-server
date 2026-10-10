// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

internal static partial class ProgramLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Redis multiplexer initialized without an active connection. The client will continue retrying in the background.")]
    public static partial void RedisStartupConnectionInactive(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to connect to Redis at startup. RedisCacheService will operate in fallback mode.")]
    public static partial void RedisStartupConnectionFailed(ILogger logger, Exception exception);

    /// <summary>
    /// Startup line for an accepted Redis durability attestation (information; owner ruling
    /// 2026-10-10 makes the attestation informational rather than a capability gate).
    /// </summary>
    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Redis durability attestation: attested ({PersistenceMode}, {AcknowledgedWritePolicy}, maxmemory-policy={EvictionPolicy}). Published as limits.job.redisDurability on the capability manifest.")]
    public static partial void RedisDurabilityAttested(
        ILogger logger,
        string persistenceMode,
        string acknowledgedWritePolicy,
        string evictionPolicy);

    /// <summary>
    /// Startup line when the Redis policy could not be read, as on managed Redis that blocks
    /// <c>CONFIG</c> (AWS ElastiCache, MemoryDB). Information, not a rejection: the governed
    /// control plane and the durable job runner stay enabled.
    /// </summary>
    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Redis durability attestation: {Status} (the persistence policy could not be read: {Detail}). {Consequence} {Guidance} Jobs:RequireDurableStore=true would refuse startup on this Redis.")]
    public static partial void RedisDurabilityUnverified(
        ILogger logger,
        string status,
        string detail,
        string consequence,
        string guidance);

    /// <summary>
    /// Startup line when the Redis policy was read and is not durable (AOF off, fsync <c>no</c>,
    /// or an evicting policy). A warning so the operator sees it, never a rejection: the governed
    /// control plane and the durable job runner stay enabled.
    /// </summary>
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
