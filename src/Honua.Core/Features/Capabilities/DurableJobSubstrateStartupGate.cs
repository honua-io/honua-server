// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Capabilities;

/// <summary>
/// Marker proving the deployment is ENTITLED to a durable job substrate, registered by the
/// composition root when the bootstrap license snapshot grants <c>caching.redis</c>.
/// </summary>
/// <remarks>
/// Entitlement and durability attestation are two different gates and honua-server#4502 keeps
/// them apart. Durability decides what the server ADVERTISES; entitlement decides whether the
/// durable job substrate is composed at all. They were briefly conflated because #4141 gated the
/// job-store registration on <see cref="RedisDurabilityAttestation"/>, which the composition root
/// happens to register only when Redis is entitled — so removing that gate to stop the
/// unresolved-service startup crash would have let an unentitled deployment whose Redis DOES
/// attest compose and advertise <c>jobs.runner</c>. This marker carries the entitlement fact on
/// its own so the registration can require it without ever consulting the attestation.
/// <para>
/// Infrastructure Redis is connected in non-Development/Test deployments regardless of
/// entitlement (<c>requiresDurableDistributedEvents</c> forces it for distributed events), so
/// the presence of <c>IConnectionMultiplexer</c> is NOT evidence of a jobs entitlement.
/// </para>
/// </remarks>
public sealed class DurableJobSubstrateEntitlement
{
    /// <summary>The capability id this entitlement unlocks.</summary>
    public const string CapabilityId = CapabilityUnavailableCodes.DurableJobsCapability;
}

/// <summary>
/// Operator policy for what an unattested Redis durability inspection means at startup.
/// </summary>
/// <remarks>
/// By default the attestation is <em>information</em> (owner ruling, 2026-10-10): a connected
/// Redis composes the durable job runner and the governed proposal control plane, the capability
/// manifest advertises both, and the attestation outcome is published as
/// <c>limits.job.redisDurability</c> (<c>attested</c> / <c>unverified</c> / <c>not-durable</c>)
/// plus one startup log line. Durability is an operator property of the Redis deployment.
/// Operators who would rather not run at all without a <c>CONFIG GET</c>-proven durable policy
/// opt in to <see cref="RequireDurableStore"/>, which converts any outcome other than
/// <c>attested</c> into a typed <see cref="DurableJobSubstrateNotAttestedException"/> raised by
/// the composition root (honua-server#4502). That opt-in cannot be satisfied on managed Redis
/// that blocks <c>CONFIG</c> (AWS ElastiCache, MemoryDB): leave it off there.
/// </remarks>
public sealed class JobDurabilityOptions
{
    /// <summary>Configuration section name (<c>Jobs</c>).</summary>
    public const string SectionName = "Jobs";

    /// <summary>
    /// When <see langword="true"/>, startup fails with
    /// <see cref="DurableJobSubstrateNotAttestedException"/> unless the Redis durability
    /// attestation is <c>attested</c> (AOF on, <c>appendfsync</c> everysec/always,
    /// <c>maxmemory-policy noeviction</c>, all read through <c>CONFIG GET</c>). Defaults to
    /// <see langword="false"/>, under which the attestation is informational only. Cannot be
    /// satisfied on managed Redis that blocks <c>CONFIG</c> (AWS ElastiCache, MemoryDB).
    /// </summary>
    public bool RequireDurableStore { get; set; }
}

/// <summary>
/// Raised by the composition root when <see cref="JobDurabilityOptions.RequireDurableStore"/>
/// is set and the Redis durability attestation was not accepted.
/// </summary>
/// <remarks>
/// This is the ONLY sanctioned way an unattested durable substrate may stop the process. It
/// carries the typed cause and its remediation so the operator reads one actionable line
/// rather than a wall of <c>Unable to resolve service for type 'IExecutionJobStore'</c>
/// descriptor-validation failures (honua-server#4502).
/// </remarks>
public sealed class DurableJobSubstrateNotAttestedException : Exception
{
    /// <summary>Initializes a new instance with the default message.</summary>
    public DurableJobSubstrateNotAttestedException()
        : this(DurableJobSubstrateCause.RedisAttestationUnavailable, detail: null)
    {
    }

    /// <summary>Initializes a new instance with a caller-supplied message.</summary>
    /// <param name="message">The message.</param>
    public DurableJobSubstrateNotAttestedException(string message)
        : base(message)
    {
        Cause = DurableJobSubstrateCause.RedisAttestationUnavailable;
        Remediation = DurableJobSubstrateRemediation.For(Cause);
    }

    /// <summary>Initializes a new instance with a caller-supplied message and inner exception.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The inner exception.</param>
    public DurableJobSubstrateNotAttestedException(string message, Exception? innerException)
        : base(message, innerException)
    {
        Cause = DurableJobSubstrateCause.RedisAttestationUnavailable;
        Remediation = DurableJobSubstrateRemediation.For(Cause);
    }

    /// <summary>Initializes a new instance for a classified attestation failure.</summary>
    /// <param name="cause">The classified cause.</param>
    /// <param name="detail">Machine-observed detail from the attestation, when available.</param>
    public DurableJobSubstrateNotAttestedException(DurableJobSubstrateCause cause, string? detail)
        : base(BuildMessage(cause, detail))
    {
        Cause = cause;
        Detail = detail;
        Remediation = DurableJobSubstrateRemediation.For(cause);
    }

    /// <summary>The classified reason the durable job substrate is not attested.</summary>
    public DurableJobSubstrateCause Cause { get; }

    /// <summary>Machine-observed attestation detail (e.g. <c>appendonly=no, aof_enabled=0</c>).</summary>
    public string? Detail { get; }

    /// <summary>Operator-facing remediation for <see cref="Cause"/>.</summary>
    public string Remediation { get; } = string.Empty;

    /// <summary>The capability-manifest id this failure disables.</summary>
    public const string CapabilityId = CapabilityUnavailableCodes.DurableJobsCapability;

    private static string BuildMessage(DurableJobSubstrateCause cause, string? detail)
        => $"{JobDurabilityOptions.SectionName}:{nameof(JobDurabilityOptions.RequireDurableStore)} is enabled, "
            + $"but the durable job substrate is not attested ({cause}"
            + (string.IsNullOrWhiteSpace(detail) ? ")" : $": {detail})")
            + $". {DurableJobSubstrateRemediation.For(cause)} "
            + $"Alternatively, clear {JobDurabilityOptions.SectionName}:{nameof(JobDurabilityOptions.RequireDurableStore)} "
            + "to treat the attestation as information (the default; required on managed Redis that "
            + "blocks CONFIG, such as AWS ElastiCache and MemoryDB).";
}

/// <summary>
/// Cause-specific remediation for an unattested or uncomposed durable job substrate, plus the
/// single sentence that states the operating consequence of running non-durable.
/// </summary>
/// <remarks>
/// Shared by the startup warning, the typed startup refusal, and the health-check roll-up so
/// the three surfaces cannot drift — the same discipline
/// <see cref="CapabilityUnavailableCodes"/> applies to the request-time refusals.
/// </remarks>
public static class DurableJobSubstrateRemediation
{
    /// <summary>
    /// What running with an unattested Redis substrate actually means for the operator. Stated
    /// once so the startup log line and the health-check data agree.
    /// </summary>
    public const string NonDurableConsequence =
        "The governed control plane and the durable job runner stay enabled (operations.proposals "
        + "and jobs.runner are advertised); durability is an operator property of the Redis "
        + "deployment, and the capability manifest publishes this outcome as "
        + "limits.job.redisDurability.";

    /// <summary>Returns the remediation sentence for a classified cause.</summary>
    /// <param name="cause">The classified cause.</param>
    /// <returns>An operator-facing remediation sentence.</returns>
    public static string For(DurableJobSubstrateCause cause)
        => cause switch
        {
            DurableJobSubstrateCause.Available =>
                "No action required; Redis durability is attested.",

            DurableJobSubstrateCause.RedisPersistenceDisabled =>
                "Enable Redis append-only persistence (start Redis with "
                + "'redis-server --appendonly yes', or set 'appendonly yes' in redis.conf) so "
                + "acknowledged control-plane writes survive a restart.",

            DurableJobSubstrateCause.RedisWritePolicyUnsafe =>
                "Set the Redis 'appendfsync' policy to 'everysec' or 'always'; under 'no' an "
                + "acknowledged write can still be lost in the OS page cache.",

            DurableJobSubstrateCause.RedisEvictionPolicyUnsafe =>
                "Set the Redis 'maxmemory-policy' to 'noeviction' so durable control-plane keys "
                + "are never evicted under memory pressure.",

            DurableJobSubstrateCause.RedisAttestationUnavailable =>
                "No action is required on managed Redis that blocks CONFIG (AWS ElastiCache, "
                + "MemoryDB): rely on the service's own durability (replication, snapshots, "
                + "Multi-AZ, MemoryDB's transaction log). On self-managed Redis, let the server's "
                + "Redis user read the persistence policy ('INFO persistence' plus 'CONFIG GET' "
                + "for appendonly, appendfsync and maxmemory-policy) to have it attested.",

            DurableJobSubstrateCause.RedisUnreachable =>
                "Redis is configured but did not connect at startup. Check the 'redis' "
                + "connection string, network reachability, TLS and credentials, then restart.",

            DurableJobSubstrateCause.RedisNotConfigured =>
                CapabilityUnavailableCodes.RedisRemediation,

            DurableJobSubstrateCause.RedisNotEntitled =>
                CapabilityUnavailableCodes.EntitlementRemediation,

            DurableJobSubstrateCause.RuntimeIncomplete =>
                CapabilityUnavailableCodes.RuntimeIncompleteRemediation,

            _ => CapabilityUnavailableCodes.RedisRemediation,
        };
}

/// <summary>
/// The one startup decision an unattested durable job substrate is allowed to make.
/// </summary>
/// <remarks>
/// honua-server#4502: before this gate existed, a rejected attestation composed the durable
/// job store OUT while every consumer of <c>IExecutionJobStore</c> stayed registered, so the
/// process aborted during <c>ServiceProvider</c> descriptor validation — 31 unresolved-service
/// failures and exit 139, before the server ever bound a port. The registration contract is
/// now the inverse: the store is always composed alongside Redis, and only this explicit,
/// opt-in gate may refuse to start — with a typed error naming the cause.
/// </remarks>
public static class DurableJobSubstrateStartupGate
{
    /// <summary>
    /// Throws when the operator required a durable job store and the substrate could not
    /// attest durability. A no-op otherwise, including the default policy, under which the attestation is information.
    /// </summary>
    /// <param name="options">The startup-resolved substrate facts.</param>
    /// <param name="requireDurableStore">The <c>Jobs:RequireDurableStore</c> policy.</param>
    /// <param name="detail">Machine-observed attestation detail, when available.</param>
    /// <exception cref="DurableJobSubstrateNotAttestedException">
    /// Thrown when <paramref name="requireDurableStore"/> is set and durability is not attested.
    /// </exception>
    public static void EnsureSatisfied(
        DurableJobSubstrateOptions options,
        bool requireDurableStore,
        string? detail = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!requireDurableStore || options.RedisDurabilityAttestation is not null)
        {
            return;
        }

        // The configuration facts decide first (absent, unentitled, unreachable Redis);
        // jobStorePresent/jobQueuePresent are not knowable before the provider is built and are
        // not what decides this. For a connected, entitled Redis report the attestation outcome
        // itself (unverified / not-durable) rather than a generic incomplete runtime.
        var cause = options.Classify(jobStorePresent: false, jobQueuePresent: false);
        if (cause == DurableJobSubstrateCause.RuntimeIncomplete && options.RedisDurabilityFailure is { } outcome)
        {
            cause = outcome;
        }

        throw new DurableJobSubstrateNotAttestedException(cause, detail);
    }
}
