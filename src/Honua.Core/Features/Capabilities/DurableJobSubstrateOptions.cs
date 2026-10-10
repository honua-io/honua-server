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

    /// <summary>
    /// Attestation outcome, not a composition cause: Redis policy could not be read (managed
    /// Redis such as AWS ElastiCache and MemoryDB block <c>CONFIG</c>), so durability is
    /// <see cref="RedisDurabilityStatuses.Unverified"/>. Informational since the 2026-10-10
    /// owner ruling; it no longer withholds any capability.
    /// </summary>
    RedisAttestationUnavailable,

    /// <summary>Attestation outcome (informational): Redis persistence is disabled.</summary>
    RedisPersistenceDisabled,

    /// <summary>Attestation outcome (informational): the acknowledged-write fsync policy is <c>no</c>.</summary>
    RedisWritePolicyUnsafe,

    /// <summary>Attestation outcome (informational): Redis may evict keys under memory pressure.</summary>
    RedisEvictionPolicyUnsafe,

    /// <summary>
    /// Redis is configured but did not connect at startup, so the Redis-backed control plane is
    /// not usable. Unlike the attestation outcomes above this IS a composition cause: it is the
    /// "Redis is genuinely absent" case the typed capability-unavailable refusals describe.
    /// </summary>
    RedisUnreachable,
}

/// <summary>
/// The published Redis durability outcome (<c>limits.job.redisDurability.status</c> on the
/// capability manifest).
/// </summary>
/// <remarks>
/// Owner ruling (2026-10-10): the governed proposal control plane and the durable job runner
/// require Redis to be present and working, not a <c>CONFIG GET</c>-proven durable policy.
/// Durability is an operator property of the Redis deployment (AOF, replication, snapshots,
/// a managed service's own guarantees), so the startup attestation is published as information
/// and never withholds a capability.
/// </remarks>
public static class RedisDurabilityStatuses
{
    /// <summary>AOF on, <c>appendfsync</c> everysec/always, <c>maxmemory-policy noeviction</c>.</summary>
    public const string Attested = "attested";

    /// <summary>
    /// The policy could not be read (typically <c>CONFIG</c> is blocked, as on AWS ElastiCache and
    /// MemoryDB), so durability is neither proven nor disproven.
    /// </summary>
    public const string Unverified = "unverified";

    /// <summary>The policy was read and is not durable (AOF off, fsync <c>no</c>, or an evicting policy).</summary>
    public const string NotDurable = "not-durable";

    /// <summary>Maps an attestation outcome to its published status.</summary>
    /// <param name="attested">Whether the attestation was accepted.</param>
    /// <param name="cause">The rejection cause, when not accepted.</param>
    /// <returns>The status, or <see langword="null"/> when no attestation outcome exists.</returns>
    public static string? For(bool attested, DurableJobSubstrateCause? cause)
        => attested
            ? Attested
            : cause switch
            {
                DurableJobSubstrateCause.RedisAttestationUnavailable => Unverified,
                DurableJobSubstrateCause.RedisPersistenceDisabled
                    or DurableJobSubstrateCause.RedisWritePolicyUnsafe
                    or DurableJobSubstrateCause.RedisEvictionPolicyUnsafe => NotDurable,
                _ => null,
            };
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

    /// <summary>
    /// The accepted startup attestation, or <see langword="null"/> when durability was not
    /// attested. Informational: it is published, never used to withhold a capability.
    /// </summary>
    public RedisDurabilityAttestation? RedisDurabilityAttestation { get; set; }

    /// <summary>
    /// Typed reason the Redis durability attestation was not accepted (informational), or
    /// <see cref="DurableJobSubstrateCause.RedisUnreachable"/> when Redis did not connect at
    /// startup (which does withhold the Redis-backed capabilities).
    /// </summary>
    public DurableJobSubstrateCause? RedisDurabilityFailure { get; set; }

    /// <summary>
    /// Machine-observed detail of a non-accepted attestation (for example
    /// <c>ERR unknown command 'CONFIG'</c> or <c>appendonly=no, aof_enabled=0</c>). Logged at
    /// startup only; never published on the anonymous capability manifest because a client
    /// error message can name internal endpoints.
    /// </summary>
    public string? RedisDurabilityDetail { get; set; }

    /// <summary>
    /// The published durability status (<see cref="RedisDurabilityStatuses"/>), or
    /// <see langword="null"/> when no attestation ran against a connected Redis.
    /// </summary>
    public string? RedisDurabilityStatus
        => RedisDurabilityStatuses.For(RedisDurabilityAttestation is not null, RedisDurabilityFailure);

    /// <summary>
    /// Classifies why the substrate is unavailable, given whether the composed runtime actually
    /// resolved a durable job store and a runnable queue.
    /// </summary>
    /// <param name="jobStorePresent">Whether <c>IExecutionJobStore</c> resolved.</param>
    /// <param name="jobQueuePresent">Whether <c>IJobQueue</c> resolved.</param>
    /// <returns>The cause to report to callers.</returns>
    public DurableJobSubstrateCause Classify(bool jobStorePresent, bool jobQueuePresent)
    {
        // Entitlement is checked BEFORE durability, and fails closed (honua-server#4502). The
        // durability inspection runs against whatever infrastructure Redis is connected, which in
        // non-Development/Test deployments happens even when Redis is unentitled — so an
        // unentitled deployment whose Redis DOES attest would otherwise report Available and
        // advertise 'jobs.runner'. A present store never upgrades a missing entitlement.
        if (!RedisEntitled && RedisConfigured)
        {
            return DurableJobSubstrateCause.RedisNotEntitled;
        }

        // Owner ruling (2026-10-10): a CONNECTED Redis enables the durable job runner and the
        // governed proposal control plane whatever the durability attestation said. The
        // attestation outcome is published (RedisDurabilityStatus) and logged, never classified
        // here; only a Redis that is absent or did not connect withholds the capability.
        if (jobStorePresent && jobQueuePresent)
        {
            if (!RedisConfigured)
            {
                return DurableJobSubstrateCause.RedisNotConfigured;
            }

            return RedisDurabilityFailure == DurableJobSubstrateCause.RedisUnreachable
                ? DurableJobSubstrateCause.RedisUnreachable
                : DurableJobSubstrateCause.Available;
        }

        if (!RedisConfigured)
        {
            return DurableJobSubstrateCause.RedisNotConfigured;
        }

        return RedisEntitled
            ? RedisDurabilityFailure == DurableJobSubstrateCause.RedisUnreachable
                ? DurableJobSubstrateCause.RedisUnreachable
                : DurableJobSubstrateCause.RuntimeIncomplete
            : DurableJobSubstrateCause.RedisNotEntitled;
    }
}
