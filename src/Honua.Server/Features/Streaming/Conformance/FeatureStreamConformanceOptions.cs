// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Streaming.Conformance;

// The mutation surface stays off unless an operator pointed it at a dedicated source.
public sealed class FeatureStreamConformanceOptions
{
    public const string SectionName = "FeatureStreaming:Conformance";

    // The surface fails closed unless an operator enabled it.
    public bool Enabled { get; set; }

    // There is no default service, so a typo fails closed.
    public string? ServiceId { get; set; }

    public int LayerId { get; set; }

    public string RunIdField { get; set; } = "conformance_run_id";

    public string? LabelField { get; set; } = "conformance_label";

    // The default lease is exclusive because a second run would appear in the first run's baseline.
    public int MaxConcurrentRuns { get; set; } = 1;

    public TimeSpan RunTtl { get; set; } = TimeSpan.FromMinutes(5);

    public TimeSpan MaxRunTtl { get; set; } = TimeSpan.FromMinutes(30);

    public int MaxMutationsPerRun { get; set; } = 32;

    public int MaxRecordsPerRun { get; set; } = 8;

    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromMinutes(1);

    public int MaxSweepRecords { get; set; } = 2000;

    // The workflow refuses to start when the deployment has no immutable revision.
    public bool RequireDeploymentRevision { get; set; } = true;
}

internal sealed class FeatureStreamConformanceOptionsValidator : IValidateOptions<FeatureStreamConformanceOptions>
{
    private const int MaxSupportedConcurrentRuns = 16;

    public ValidateOptionsResult Validate(string? name, FeatureStreamConformanceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        if (!options.Enabled)
        {
            // A disabled subsystem is never consulted, so an incomplete configuration must
            // not block startup for the overwhelming majority of deployments that will never
            // turn this on.
            return ValidateOptionsResult.Success;
        }

        if (string.IsNullOrWhiteSpace(options.ServiceId))
        {
            failures.Add($"{SectionKey(nameof(FeatureStreamConformanceOptions.ServiceId))} is required when the controlled-conformance workflow is enabled.");
        }

        if (options.LayerId < 0)
        {
            failures.Add($"{SectionKey(nameof(FeatureStreamConformanceOptions.LayerId))} must be a non-negative integer.");
        }

        if (string.IsNullOrWhiteSpace(options.RunIdField))
        {
            failures.Add($"{SectionKey(nameof(FeatureStreamConformanceOptions.RunIdField))} is required: controlled records must carry an ownership marker.");
        }

        if (options.MaxConcurrentRuns is < 1 or > MaxSupportedConcurrentRuns)
        {
            failures.Add($"{SectionKey(nameof(FeatureStreamConformanceOptions.MaxConcurrentRuns))} must be between 1 and {MaxSupportedConcurrentRuns}.");
        }

        if (options.RunTtl <= TimeSpan.Zero)
        {
            failures.Add($"{SectionKey(nameof(FeatureStreamConformanceOptions.RunTtl))} must be a positive duration.");
        }

        if (options.MaxRunTtl < options.RunTtl)
        {
            failures.Add($"{SectionKey(nameof(FeatureStreamConformanceOptions.MaxRunTtl))} must be greater than or equal to RunTtl.");
        }

        if (options.MaxMutationsPerRun <= 0)
        {
            failures.Add($"{SectionKey(nameof(FeatureStreamConformanceOptions.MaxMutationsPerRun))} must be a positive integer.");
        }

        if (options.MaxRecordsPerRun <= 0)
        {
            failures.Add($"{SectionKey(nameof(FeatureStreamConformanceOptions.MaxRecordsPerRun))} must be a positive integer.");
        }

        if (options.SweepInterval <= TimeSpan.Zero)
        {
            failures.Add($"{SectionKey(nameof(FeatureStreamConformanceOptions.SweepInterval))} must be a positive duration.");
        }

        if (options.MaxSweepRecords <= 0)
        {
            failures.Add($"{SectionKey(nameof(FeatureStreamConformanceOptions.MaxSweepRecords))} must be a positive integer.");
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }

    private static string SectionKey(string property)
        => string.Concat(FeatureStreamConformanceOptions.SectionName, ":", property);
}
