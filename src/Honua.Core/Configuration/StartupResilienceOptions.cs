// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Configuration;

/// <summary>
/// Controls how the host behaves when the database is unavailable during startup.
/// </summary>
/// <remarks>
/// Defaults preserve the classic fail-fast behavior (crash on startup so Kubernetes shows a clear
/// <c>CrashLoopBackOff</c>). Serverless and single-node deployments that must keep serving non-database
/// routes during a database outage opt in by setting <see cref="DegradedStartEnabled"/> to
/// <see langword="true"/> (config key <c>Database:StartupResilience:DegradedStartEnabled</c> or env var
/// <c>HONUA_DB_DEGRADED_START</c>). Degraded start only suppresses the crash for <em>transient</em>
/// connectivity failures; genuine misconfiguration (bad migration SQL, incompatible PostGIS) still
/// fails loudly.
/// </remarks>
public sealed record StartupResilienceOptions
{
    /// <summary>
    /// Configuration section name for binding from <c>appsettings</c>/environment.
    /// </summary>
    public const string SectionName = "Database:StartupResilience";

    /// <summary>
    /// When <see langword="true"/>, transient database-connectivity failures during startup
    /// (preflight + migrations) do not crash the process; the host starts in a degraded mode that
    /// serves non-database routes and retries connectivity in the background. Default
    /// <see langword="false"/> (fail fast).
    /// </summary>
    public bool DegradedStartEnabled { get; init; }

    /// <summary>
    /// Whether the background retry loop attempts migrations when degraded start was triggered by a
    /// migration failure. Default <see langword="true"/>.
    /// </summary>
    public bool RetryMigrationsInBackground { get; init; } = true;

    /// <summary>
    /// Base delay for the first background retry attempt. Default 2 seconds.
    /// </summary>
    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Ceiling for any single background retry delay. Default 60 seconds.
    /// </summary>
    public TimeSpan RetryMaxDelay { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Maximum number of background retry attempts before giving up (the process keeps running but
    /// stops retrying). 0 means retry indefinitely. Default 0.
    /// </summary>
    public int MaxRetryAttempts { get; init; }
}
