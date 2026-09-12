// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

internal static partial class ProgramLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Redis multiplexer initialized without an active connection. The client will continue retrying in the background.")]
    public static partial void RedisStartupConnectionInactive(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to connect to Redis at startup. RedisCacheService will operate in fallback mode.")]
    public static partial void RedisStartupConnectionFailed(ILogger logger, Exception exception);

    /// <summary>
    /// The one startup line an operator needs when Redis durability attestation is rejected
    /// (honua-server#4502). Names the typed cause, the machine-observed detail, what running
    /// non-durable actually means, and how to fix it — so the degraded boot is a one-line
    /// diagnosis rather than the 31 unresolved-service descriptor failures it used to be.
    /// </summary>
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Redis durability attestation was REJECTED ({Cause}: {Detail}). {Consequence} Remediation: {Remediation} Set Jobs:RequireDurableStore=true to fail startup instead of degrading.")]
    public static partial void RedisDurabilityNotAttested(
        ILogger logger,
        Honua.Core.Features.Capabilities.DurableJobSubstrateCause cause,
        string detail,
        string consequence,
        string remediation);
}
