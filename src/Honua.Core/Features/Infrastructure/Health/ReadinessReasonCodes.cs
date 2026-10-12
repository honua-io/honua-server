// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Health;

/// <summary>
/// Stable, machine-readable reason codes for a failed readiness check. The served readiness probe
/// returns the code in the <see cref="HeaderName"/> response header so orchestration and
/// qualification harnesses can assert why a node is not ready without parsing prose.
/// </summary>
public static class ReadinessReasonCodes
{
    /// <summary>Response header carrying the reason code of a not-ready probe answer.</summary>
    public const string HeaderName = "X-Honua-Readiness-Reason";

    /// <summary>Database migrations failed.</summary>
    public const string MigrationsFailed = "migrations-failed";

    /// <summary>Database migrations are still running.</summary>
    public const string MigrationsInProgress = "migrations-in-progress";

    /// <summary>Database migrations have not completed.</summary>
    public const string MigrationsNotCompleted = "migrations-not-completed";

    /// <summary>
    /// The host started in degraded mode because the database was transiently unavailable at boot
    /// (for example SQLSTATE 53300, no free connection slots) and is retrying in the background.
    /// </summary>
    public const string DatabaseStartupDegraded = "database-startup-degraded";

    /// <summary>
    /// The host started in degraded mode because the database schema is behind this binary (a
    /// required numbered migration is not applied yet, for example a cold start before the migration
    /// task ran) and is re-checking in the background until the migrations land (honua-server#5916).
    /// </summary>
    public const string SchemaFloor = "schema-floor";

    /// <summary>The database is unavailable.</summary>
    public const string DatabaseUnavailable = "database-unavailable";

    /// <summary>A configured cache dependency is unavailable.</summary>
    public const string CacheUnavailable = "cache-unavailable";

    /// <summary>Feature-change event storage cannot persist events.</summary>
    public const string FeatureChangeEventStorageUnavailable = "feature-change-event-storage-unavailable";

    /// <summary>The alert dispatch loop appears hung.</summary>
    public const string AlertDispatchStalled = "alert-dispatch-stalled";

    /// <summary>Alert evaluation is stalled (no leader, or a hung leader).</summary>
    public const string AlertEvaluationStalled = "alert-evaluation-stalled";

    /// <summary>
    /// The geoprocessing referenced-output store attestation marker is missing, corrupted or
    /// mismatched (honua-server#4805).
    /// </summary>
    public const string GeoprocessingOutputStoreAttestationUnavailable = "gp-output-store-attestation-unavailable";
}
