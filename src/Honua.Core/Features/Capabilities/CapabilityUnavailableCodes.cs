// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Capabilities;

/// <summary>
/// Stable, machine-readable vocabulary for the <em>capability-unavailable</em> refusal an
/// adapter emits when a required infrastructure dependency was never composed
/// (honua-release#202).
/// </summary>
/// <remarks>
/// <para>
/// The 2026.1 install topology is: <strong>PostGIS is mandatory, Redis is optional.</strong>
/// Without Redis the multi-layer cache falls back to the in-memory provider and read paths
/// keep serving, but the durable job/workflow substrate is not composed. When that happens a
/// terminal agent must get an answer it can branch on without parsing prose: the refusal names
/// the missing dependency, the capability id it disables (joinable to
/// <c>GET /api/v1/capabilities/manifest</c>), and where the remediation is written down.
/// A hang, an untyped 500, or a queued-but-never-drained job all fail that contract.
/// </para>
/// <para>
/// These constants are the single source of the strings emitted across the problem+json,
/// GeoServices, and MCP error envelopes so the three surfaces cannot drift.
/// </para>
/// </remarks>
public static class CapabilityUnavailableCodes
{
    /// <summary>RFC 7807 <c>type</c> URI for the capability-unavailable problem family.</summary>
    public const string ProblemType = "https://honua.io/problems/capability-unavailable";

    /// <summary>
    /// Machine-readable error code carried as the problem <c>code</c> extension member (and as
    /// the manifest reason code for a capability disabled by a missing dependency).
    /// </summary>
    public const string ErrorCode = "dependency-unavailable";

    /// <summary>
    /// Machine-readable error code for a capability that is composed-out because the licence does
    /// not entitle it, rather than because a dependency is missing. Matches the capability
    /// manifest's existing <c>license-required</c> reason code so a client can join the two.
    /// </summary>
    public const string EntitlementErrorCode = "license-required";

    /// <summary>Identifier of the dependency backing the durable job/workflow substrate.</summary>
    public const string RedisDependency = "redis";

    /// <summary>
    /// Identifier of the runnable job queue. Reported when a durable job store is composed without
    /// one, which would let a submission persist and never drain.
    /// </summary>
    public const string JobQueueDependency = "job-queue";

    /// <summary>
    /// The Pro entitlement that gates registration of <c>IConnectionMultiplexer</c> and therefore
    /// the whole durable job substrate.
    /// </summary>
    public const string RedisCacheEntitlement = "caching.redis";

    /// <summary>The capability-manifest id the durable job substrate backs.</summary>
    public const string DurableJobsCapability = "jobs.runner";

    /// <summary>Operator-facing remediation for a server started without Redis.</summary>
    public const string RedisRemediation =
        "Set ConnectionStrings__Redis to a reachable Redis instance and restart the server. " +
        "With the repository compose files, drop the '-f docker-compose.no-redis.yml' override " +
        "and run 'docker compose up -d' again; PostGIS-backed metadata state is preserved.";

    /// <summary>Canonical documentation anchor for the Redis-optional install decision.</summary>
    public const string RedisRemediationRef =
        "https://docs.honua.io/guides/deploy/docker-compose#redis-is-optional-postgis-is-not";

    /// <summary>
    /// Remediation for a host where Redis IS configured but the Pro <c>caching.redis</c>
    /// entitlement is absent, so the substrate was never composed. Deliberately does not tell the
    /// operator to configure Redis — Redis is already there, and doing it again changes nothing.
    /// </summary>
    public const string EntitlementRemediation =
        "Redis is configured but the Pro 'caching.redis' entitlement is not active, so the " +
        "durable job substrate was not composed. Install a licence that includes " +
        "'caching.redis'; outside Production you can instead set Licensing:DevGrantEdition=Pro " +
        "(HONUA_DEV_GRANT_EDITION=Pro for the repository compose files). Restart the server " +
        "after either change.";

    /// <summary>Documentation anchor for the entitlement remediation.</summary>
    public const string EntitlementRemediationRef =
        "https://docs.honua.io/guides/deploy/docker-compose#redis-is-configured-but-not-entitled";

    /// <summary>Detail sentence for a refusal caused by an unentitled (but present) Redis.</summary>
    public const string UnentitledRedisDetail =
        "Durable geoprocessing jobs and workflows require the Pro 'caching.redis' entitlement. " +
        "Redis is configured on this server, but the active licence does not entitle it, so the " +
        "durable job substrate was never composed and the request is refused up front instead of " +
        "being queued and never run.";

    /// <summary>
    /// Remediation for a host whose durable job substrate is composed only in part — a job store
    /// without a runnable queue, which would let a submission persist and never drain.
    /// </summary>
    public const string RuntimeIncompleteRemediation =
        "A durable job store is registered without a runnable job queue, so submitted jobs could " +
        "never drain. Register the full job orchestration substrate (AddJobOrchestration) or " +
        "remove the partial registration.";

    /// <summary>Detail sentence for a refusal caused by an incomplete job substrate.</summary>
    public const string RuntimeIncompleteDetail =
        "The durable job substrate is incomplete: a job store is registered without a runnable " +
        "job queue, so a submitted job would be persisted and never drain. The request is refused " +
        "rather than accepted into a queue that does not exist.";

    /// <summary>Short human-readable title shared by every capability-unavailable refusal.</summary>
    public const string Title = "Capability unavailable";

    /// <summary>Detail sentence for a refusal caused by the absent durable job store.</summary>
    public const string DurableJobStoreDetail =
        "Durable geoprocessing jobs and workflows require a Redis-backed job store. This server " +
        "was started without a Redis connection, so the request is refused up front instead of " +
        "being queued and never run.";

    /// <summary>
    /// Detail sentence for a control-plane refusal on a host where Redis is present but the Pro
    /// <c>caching.redis</c> entitlement is not, so "configure Redis" would be the wrong fix.
    /// </summary>
    /// <remarks>
    /// Like <see cref="DurableControlPlaneDetail"/>, refusals on this path carry no
    /// <c>capability</c> member — see that constant's remarks.
    /// </remarks>
    public const string UnentitledControlPlaneDetail =
        "The operation proposal and approval control plane requires the Pro 'caching.redis' " +
        "entitlement. Redis is configured on this server, but the active licence does not entitle " +
        "it, so the durable control plane was never composed.";

    /// <summary>Detail sentence for a refusal caused by the absent durable control plane.</summary>
    /// <remarks>
    /// Refusals on this path deliberately carry no <c>capability</c> member: the manifest has no
    /// capability id covering the proposal/approval control plane, and naming an unrelated id
    /// (<c>operate.status</c>, which stays available) would send a client to a claim that
    /// contradicts the refusal. Omitting the field is the honest answer until the manifest gains
    /// an id for this surface.
    /// </remarks>
    public const string DurableControlPlaneDetail =
        "The operation proposal and approval control plane requires a Redis-backed durable " +
        "store. This server was started without a Redis connection, so proposals cannot be " +
        "listed, inspected, approved, or rejected.";
}
