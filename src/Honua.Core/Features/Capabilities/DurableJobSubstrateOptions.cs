// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Capabilities;

/// <summary>
/// Why the durable job/workflow substrate is or is not composed on this host.
/// </summary>
/// <remarks>
/// The distinction matters because the remediation differs. Telling an operator to "configure
/// Redis" when Redis is already running and only the licence is missing is remediation that
/// cannot work, which is the failure mode honua-release#202 exists to prevent.
/// </remarks>
public enum DurableJobSubstrateCause
{
    /// <summary>The substrate is composed; durable jobs and workflows are runnable.</summary>
    Available,

    /// <summary>No Redis connection string is configured, so nothing Redis-backed was wired.</summary>
    RedisNotConfigured,

    /// <summary>
    /// Redis is configured, but the bootstrap licence lacks the Pro <c>caching.redis</c>
    /// entitlement, so <c>IConnectionMultiplexer</c> — and therefore the durable job store and
    /// queue — were never registered. Adding Redis cannot fix this; a licence can.
    /// </summary>
    RedisNotEntitled,

    /// <summary>
    /// Redis is configured and entitled, but the composed substrate is incomplete — a durable job
    /// store is present without a runnable queue, so submissions would persist and never drain.
    /// </summary>
    RuntimeIncomplete,

    /// <summary>Redis policy could not be read, so durability cannot be established.</summary>
    RedisAttestationUnavailable,

    /// <summary>Redis persistence is disabled.</summary>
    RedisPersistenceDisabled,

    /// <summary>Redis does not expose an accepted acknowledged-write persistence policy.</summary>
    RedisWritePolicyUnsafe,

    /// <summary>Redis may evict durable control-plane keys under memory pressure.</summary>
    RedisEvictionPolicyUnsafe,
}

/// <summary>Machine-observed Redis durability facts safe to publish as release evidence.</summary>
public sealed record RedisDurabilityAttestation(
    string Endpoint,
    string PersistenceMode,
    string AcknowledgedWritePolicy,
    string EvictionPolicy,
    DateTimeOffset ObservedAt);

/// <summary>
/// Startup-resolved facts about the Redis-backed durable job substrate, captured once by the
/// composition root so every surface that has to explain an unavailable job runtime gives the
/// same, actionable answer (honua-release#202).
/// </summary>
/// <remarks>
/// Bound as an <see cref="Microsoft.Extensions.Options.IOptions{TOptions}"/> because it is a
/// configuration-derived fact fixed for the process lifetime, not a live collaborator: whether
/// Redis is configured and whether the licence entitles it are both decided before the service
/// provider is built (see <c>StartupConfigurationHelpers.IsRedisCacheEntitledAsync</c>).
/// </remarks>
public sealed class DurableJobSubstrateOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "DurableJobSubstrate";

    /// <summary>
    /// Whether a Redis connection string was supplied (<c>ConnectionStrings:Redis</c> or the
    /// Aspire equivalent).
    /// </summary>
    public bool RedisConfigured { get; set; }

    /// <summary>
    /// Whether the bootstrap licence grants the Pro <c>caching.redis</c> entitlement that gates
    /// registration of <c>IConnectionMultiplexer</c> and the durable job substrate.
    /// </summary>
    public bool RedisEntitled { get; set; }

    /// <summary>The accepted startup attestation, or <see langword="null"/> when Redis is unsafe.</summary>
    public RedisDurabilityAttestation? RedisDurabilityAttestation { get; set; }

    /// <summary>Typed reason an attempted Redis durability attestation was rejected.</summary>
    public DurableJobSubstrateCause? RedisDurabilityFailure { get; set; }

    /// <summary>
    /// Classifies why the substrate is unavailable, given whether the composed runtime actually
    /// resolved a durable job store and a runnable queue.
    /// </summary>
    /// <param name="jobStorePresent">Whether <c>IExecutionJobStore</c> resolved.</param>
    /// <param name="jobQueuePresent">Whether <c>IJobQueue</c> resolved.</param>
    /// <returns>The cause to report to callers.</returns>
    public DurableJobSubstrateCause Classify(bool jobStorePresent, bool jobQueuePresent)
    {
        if (jobStorePresent && jobQueuePresent)
        {
            return RedisDurabilityAttestation is not null
                ? DurableJobSubstrateCause.Available
                : RedisDurabilityFailure ?? DurableJobSubstrateCause.RedisAttestationUnavailable;
        }

        if (!RedisConfigured)
        {
            return DurableJobSubstrateCause.RedisNotConfigured;
        }

        return RedisEntitled
            ? RedisDurabilityFailure ?? DurableJobSubstrateCause.RuntimeIncomplete
            : DurableJobSubstrateCause.RedisNotEntitled;
    }
}
