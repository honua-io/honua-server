// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Configuration;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Infrastructure.Monitoring;

// A transient preflight is retried a bounded number of times, because an unbounded retry on Lambda holds a connection slot while the pool is already exhausted.
internal static class StartupDatabasePreflight
{
    internal const string DegradedStartKey = StartupResilienceOptions.SectionName + ":DegradedStartEnabled";

    internal const string DegradedStartEnvironmentKey = "HONUA_DB_DEGRADED_START";

    internal const string AwsLambdaFunctionNameKey = "AWS_LAMBDA_FUNCTION_NAME";

    internal const string RunningInContainerKey = "DOTNET_RUNNING_IN_CONTAINER";

    internal static readonly IReadOnlyList<TimeSpan> TransientRetryDelays =
    [
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromSeconds(1),
    ];

    // Lambda defaults to a degraded start so an init failure does not become a crash loop; an explicit false stays fail-fast.
    public static StartupResilienceOptions ApplyServerlessDefault(
        StartupResilienceOptions options,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(configuration);

        if (IsExplicitlyConfigured(configuration) || string.IsNullOrWhiteSpace(configuration[AwsLambdaFunctionNameKey]))
        {
            return options;
        }

        return options with { DegradedStartEnabled = true };
    }

    public static StartupResilienceOptions ApplyDevelopmentContainerDefault(
        StartupResilienceOptions options,
        IConfiguration configuration,
        bool isDevelopment)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(configuration);
        if (!isDevelopment || IsExplicitlyConfigured(configuration)
            || !bool.TryParse(configuration[RunningInContainerKey], out var inContainer) || !inContainer)
        {
            return options;
        }

        return options with { DegradedStartEnabled = true };
    }

    private static bool IsExplicitlyConfigured(IConfiguration configuration)
        => !string.IsNullOrWhiteSpace(configuration[DegradedStartKey])
            || !string.IsNullOrWhiteSpace(configuration[DegradedStartEnvironmentKey]);

    // An explicit recovery setting fails fast, including an explicit false in Development; only an unconfigured local Development path stays permissive.
    public static bool ShouldFailStartup(StartupResilienceOptions options, IConfiguration configuration, bool isDevelopment)
        => !isDevelopment || options.DegradedStartEnabled || IsExplicitlyConfigured(configuration);

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

    public static string BuildReason(string phase, string failureClass) => phase + ": " + failureClass;

    public static string DescribeCause(DatabaseCompatibilityResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var causeClass = result.CauseClass ?? DatabasePreflightCauseClasses.ConnectionUnavailable;
        return causeClass + " (" + (result.FailureClass ?? "database unavailable") + ")";
    }

    public static string BuildSchemaFloorWarning(string reason)
        => "Database schema is behind this build and degraded-start is enabled (" + reason + "). "
            + "The process is starting WITHOUT a usable schema; /healthz/ready reports 503 schema-floor and "
            + "control-plane event endpoints answer 503 until the migrations are applied, and the schema "
            + "is re-checked in the background. Run the migration task for this release.";
}
