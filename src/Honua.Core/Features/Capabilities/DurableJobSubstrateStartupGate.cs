// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Capabilities;

/// <summary>
/// Operator policy for what an unattested Redis durability inspection means at startup.
/// </summary>
/// <remarks>
/// The default is <em>degrade</em>: the durable job substrate is still composed (so every
/// consumer of <c>IExecutionJobStore</c> resolves and the server boots and serves), the
/// capability manifest reports the typed <see cref="DurableJobSubstrateCause"/> instead of
/// advertising <c>jobs.runner</c>, and one startup warning names the failure and its
/// consequence. Operators who would rather not run at all without an attested durable store
/// opt in to <see cref="RequireDurableStore"/>, which converts the rejection into a typed
/// <see cref="DurableJobSubstrateNotAttestedException"/> raised by the composition root
/// (honua-server#4502).
/// </remarks>
public sealed class JobDurabilityOptions
{
    /// <summary>Configuration section name (<c>Jobs</c>).</summary>
    public const string SectionName = "Jobs";

    /// <summary>
    /// When <see langword="true"/>, a Redis substrate whose durability was not attested fails
    /// startup with <see cref="DurableJobSubstrateNotAttestedException"/> instead of degrading
    /// to a composed-but-non-durable job store. Defaults to <see langword="false"/>.
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
            + "to start with non-durable jobs instead.";
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
    /// once so the startup warning and the health-check roll-up agree.
    /// </summary>
    public const string NonDurableConsequence =
        "Execution jobs are still accepted, served and reconciled, but the job store is NOT "
        + "durable: acknowledged job state can be lost if Redis restarts or evicts keys, and the "
        + "capability manifest will not advertise 'jobs.runner'.";

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
                "Confirm Redis is reachable and that the server's Redis user may read the "
                + "persistence policy ('INFO persistence' plus 'CONFIG GET' for appendonly, "
                + "appendfsync and maxmemory-policy).",

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
    /// attest durability. A no-op otherwise, including the default (degrade) policy.
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

        // jobStorePresent/jobQueuePresent are not knowable before the provider is built, and
        // they are not what decides this: with no accepted attestation the classification falls
        // through to the configuration facts, which is exactly the cause to report.
        throw new DurableJobSubstrateNotAttestedException(
            options.Classify(jobStorePresent: false, jobQueuePresent: false),
            detail);
    }
}
