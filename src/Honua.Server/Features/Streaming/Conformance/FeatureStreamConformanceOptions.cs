// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Streaming.Conformance;

/// <summary>
/// Configuration for the controlled-conformance mutation workflow (honua-server#3038,
/// REQ-005/REQ-006/NFR-001). Scheduled SDK conformance needs to drive a correlated
/// create/update/delete against a live deployment and observe it on every advertised
/// transport, without ever touching ordinary demo or user records.
/// </summary>
/// <remarks>
/// Every setting here is a bound, and the whole subsystem is off by default: a deployment
/// that has not deliberately provisioned a dedicated conformance source cannot be driven
/// into a mutation by any caller, however authorized.
/// </remarks>
public sealed class FeatureStreamConformanceOptions
{
    /// <summary>
    /// Configuration section name. Nested under the streaming section because the workflow
    /// exists to exercise the feature stream and shares its bounds vocabulary.
    /// </summary>
    public const string SectionName = "FeatureStreaming:Conformance";

    /// <summary>
    /// Whether this deployment provisions a controlled-conformance source. Off by default:
    /// the mutation surface fails closed unless an operator has deliberately pointed it at a
    /// dedicated source in a demo or staging environment.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Service identifier of the dedicated conformance source. Required when
    /// <see cref="Enabled"/>. There is deliberately no default: a typo must fail closed
    /// rather than silently resolve to a shared demo service.
    /// </summary>
    public string? ServiceId { get; set; }

    /// <summary>
    /// Layer identifier within <see cref="ServiceId"/> that controlled records are written to.
    /// </summary>
    public int LayerId { get; set; }

    /// <summary>
    /// Attribute that carries the run-ownership marker on every controlled record. The
    /// marker is what makes cleanup ownership-checked and lets the sweeper recover records
    /// whose owning process died.
    /// </summary>
    public string RunIdField { get; set; } = "conformance_run_id";

    /// <summary>
    /// Attribute that carries a run's client-supplied label. Optional; when unset or absent
    /// from the layer schema no label is written.
    /// </summary>
    public string? LabelField { get; set; } = "conformance_label";

    /// <summary>
    /// Maximum number of runs that may hold a lease at once. The default of 1 makes the
    /// lease exclusive, which is what a scheduled evidence run needs: a second run's records
    /// would otherwise appear in the first run's unfiltered baseline (snapshot subscriptions
    /// cannot carry attribute filters, by design — see
    /// <c>FeatureStreamEndpoints.ValidateSnapshotScope</c>), making the observed baseline
    /// non-deterministic. Raise it only when the callers tolerate a shared baseline.
    /// </summary>
    public int MaxConcurrentRuns { get; set; } = 1;

    /// <summary>
    /// Lease time-to-live. A run that has not been released by this deadline is swept:
    /// its lease is dropped and its records are deleted. This is the bound that covers
    /// runner process death, cancellation, and timeout (NFR-001).
    /// </summary>
    public TimeSpan RunTtl { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Upper bound a caller may request for its own lease TTL. A caller may ask for less
    /// than <see cref="RunTtl"/> but never more.
    /// </summary>
    public TimeSpan MaxRunTtl { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Maximum mutations one run may perform. Bounds the write load a single lease can
    /// place on the deployment regardless of whether the optional app-level rate limiter
    /// is enabled.
    /// </summary>
    public int MaxMutationsPerRun { get; set; } = 32;

    /// <summary>
    /// Maximum controlled records one run may hold at once.
    /// </summary>
    public int MaxRecordsPerRun { get; set; } = 8;

    /// <summary>
    /// Interval between TTL sweeps.
    /// </summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Maximum records examined in one sweep or one baseline digest computation. Bounds the
    /// read the sweeper and the digest can cause on a source that has grown unexpectedly.
    /// </summary>
    public int MaxSweepRecords { get; set; } = 2000;

    /// <summary>
    /// Whether a run may only be leased against a deployment that reports an immutable
    /// revision. On by default: retained evidence that cannot name the exact deployment it
    /// was produced against is not evidence, so the workflow refuses to start rather than
    /// producing an unbindable result (REQ-006).
    /// </summary>
    public bool RequireDeploymentRevision { get; set; } = true;
}

/// <summary>
/// Validates <see cref="FeatureStreamConformanceOptions"/> at startup so a half-configured
/// conformance source surfaces as a boot failure rather than a runtime surprise.
/// </summary>
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
