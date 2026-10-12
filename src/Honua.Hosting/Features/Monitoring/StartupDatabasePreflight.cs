// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Configuration;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Infrastructure.Monitoring;

/// <summary>
/// Startup policy for the database compatibility preflight when the database is transiently
/// unavailable (connection-slot exhaustion, timeouts, socket failures).
/// </summary>
/// <remarks>
/// The preflight retries a transient result a bounded number of times so a momentary blip does not
/// degrade the host, then hands the outcome to the degraded-start gate. It never retries without
/// bound: on AWS Lambda the init phase is billed, capped, and a long retry only holds a connection
/// slot attempt open while the pool is already exhausted.
/// </remarks>
internal static class StartupDatabasePreflight
{
    /// <summary>Configuration key for the degraded-start switch.</summary>
    internal const string DegradedStartKey = StartupResilienceOptions.SectionName + ":DegradedStartEnabled";

    /// <summary>Environment override for the degraded-start switch.</summary>
    internal const string DegradedStartEnvironmentKey = "HONUA_DB_DEGRADED_START";

    /// <summary>Set by the AWS Lambda runtime in every function's environment.</summary>
    internal const string AwsLambdaFunctionNameKey = "AWS_LAMBDA_FUNCTION_NAME";

    /// <summary>Delays before each retry of a transient preflight result (two quick retries).</summary>
    internal static readonly IReadOnlyList<TimeSpan> TransientRetryDelays =
    [
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromSeconds(1),
    ];

    /// <summary>
    /// Turns degraded start on for an AWS Lambda host when the operator has not set it either way.
    /// </summary>
    /// <remarks>
    /// A Lambda host that exits during init becomes a <c>Runtime.ExitError</c> crash loop: API
    /// Gateway answers 5xx (without CORS headers) on every route and each retry cold-starts another
    /// instance that asks RDS for more connections. Starting degraded instead serves non-database
    /// routes, reports <c>/healthz/ready</c> 503 with the reason, and recovers in the background.
    /// An explicit <c>false</c> keeps fail-fast. Long-lived hosts keep the fail-fast default.
    /// </remarks>
    /// <param name="options">Options bound from configuration (environment override applied).</param>
    /// <param name="configuration">Application configuration, environment variables included.</param>
    /// <returns>The effective options.</returns>
    public static StartupResilienceOptions ApplyServerlessDefault(
        StartupResilienceOptions options,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(configuration);

        var explicitlyConfigured = !string.IsNullOrWhiteSpace(configuration[DegradedStartKey])
            || !string.IsNullOrWhiteSpace(configuration[DegradedStartEnvironmentKey]);
        if (explicitlyConfigured || string.IsNullOrWhiteSpace(configuration[AwsLambdaFunctionNameKey]))
        {
            return options;
        }

        return options with { DegradedStartEnabled = true };
    }

    /// <summary>
    /// Runs the compatibility check, retrying only a transient (database unavailable) result, at
    /// most once per entry in <paramref name="retryDelays"/>. Compatible and incompatible results
    /// return immediately.
    /// </summary>
    /// <param name="checker">The provider compatibility checker.</param>
    /// <param name="connectionString">Resolved connection string.</param>
    /// <param name="retryDelays">Delay before each retry; its length bounds the retries.</param>
    /// <param name="delay">Delay implementation (injected for tests).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The last result.</returns>
    public static async Task<DatabaseCompatibilityResult> CheckWithBoundedRetryAsync(
        IDatabaseCompatibilityChecker checker,
        string connectionString,
        IReadOnlyList<TimeSpan> retryDelays,
        Func<TimeSpan, CancellationToken, Task> delay,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(checker);
        ArgumentNullException.ThrowIfNull(retryDelays);
        ArgumentNullException.ThrowIfNull(delay);

        var result = await checker.CheckCompatibilityAsync(connectionString, cancellationToken).ConfigureAwait(false);
        foreach (var retryDelay in retryDelays)
        {
            if (!result.IsTransientFailure)
            {
                break;
            }

            await delay(retryDelay, cancellationToken).ConfigureAwait(false);
            result = await checker.CheckCompatibilityAsync(connectionString, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>Builds the sanitized degraded-start reason shown by readiness.</summary>
    /// <param name="phase">Startup phase (for example <c>PostGIS preflight</c>).</param>
    /// <param name="failureClass">Sanitized failure class.</param>
    /// <returns>The reason, such as <c>PostGIS preflight: SQLSTATE 53300 (class 53: insufficient resources)</c>.</returns>
    public static string BuildReason(string phase, string failureClass) => phase + ": " + failureClass;

    /// <summary>
    /// Names the cause class and the sanitized failure class of a degradable preflight result, for
    /// example <c>connection-borrow-timeout (timeout)</c> (honua-server#5916).
    /// </summary>
    /// <param name="result">A degradable (<see cref="DatabaseCompatibilityResult.IsTransientFailure"/>) result.</param>
    /// <returns>The description used in the degraded-start log line and readiness reason.</returns>
    public static string DescribeCause(DatabaseCompatibilityResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var causeClass = result.CauseClass ?? DatabasePreflightCauseClasses.ConnectionUnavailable;
        return causeClass + " (" + (result.FailureClass ?? "database unavailable") + ")";
    }

    /// <summary>
    /// Builds the startup warning for a schema-floor degraded start: the database is reachable but a
    /// required migration is not applied yet (honua-server#5916).
    /// </summary>
    /// <param name="reason">Sanitized schema-floor reason (the migration script name only).</param>
    /// <returns>The warning text.</returns>
    public static string BuildSchemaFloorWarning(string reason)
        => "Database schema is behind this build and degraded-start is enabled (" + reason + "). "
            + "The process is starting WITHOUT a usable schema; /healthz/ready reports 503 schema-floor and "
            + "control-plane event endpoints answer 503 until the migrations are applied, and the schema "
            + "is re-checked in the background. Run the migration task for this release.";
}
