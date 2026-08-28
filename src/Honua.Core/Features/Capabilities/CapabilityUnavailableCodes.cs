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

    /// <summary>Identifier of the dependency backing the durable job/workflow substrate.</summary>
    public const string RedisDependency = "redis";

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

    /// <summary>Short human-readable title shared by every capability-unavailable refusal.</summary>
    public const string Title = "Capability unavailable";

    /// <summary>Detail sentence for a refusal caused by the absent durable job store.</summary>
    public const string DurableJobStoreDetail =
        "Durable geoprocessing jobs and workflows require a Redis-backed job store. This server " +
        "was started without a Redis connection, so the request is refused up front instead of " +
        "being queued and never run.";

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
