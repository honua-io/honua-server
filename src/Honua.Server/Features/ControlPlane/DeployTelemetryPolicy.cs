// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.ControlPlane;

/// <summary>
/// Provider-neutral deploy telemetry policy parsed from a deploy operation's parameters.
/// </summary>
/// <remarks>
/// The policy describes <em>what</em> to evaluate (error-rate / latency / sample-count
/// thresholds plus the per-signal query strings) independently of <em>which</em> metrics
/// backend executes the queries. Prometheus connections interpret the query strings as
/// PromQL; CloudWatch connections interpret the error-rate/latency/sample-count query
/// strings as CloudWatch metric-math expressions. Presets only synthesize PromQL, so a
/// non-Prometheus provider requires explicit query overrides.
/// </remarks>
internal sealed record DeployTelemetryPolicy
{
    private const string TelemetryParameterPrefix = "telemetry.";
    private const string DefaultPrometheusJob = "honua";
    private const string DefaultCanaryPrometheusJob = "honua-canary";

    /// <summary>
    /// Telemetry-policy preset name for a serverless geoprocessing (AWS Batch) substrate deploy
    /// (honua-server#2165). GP per-job metrics are burstier and sparser than steady HTTP traffic, so
    /// this preset bakes longer and tolerates a lower sample floor, and pairs with the GP-aware
    /// anti-flap default in <see cref="DeployTelemetrySignalEvaluator"/> so a single noisy scrape
    /// does not trigger a production rollback.
    /// </summary>
    public const string GpBatchPolicyName = "gp-batch";

    /// <summary>
    /// Explicit probe-only profile (honua-server#4617): the gate is the synthetic readiness probe
    /// and/or the golden-query correctness probe, with no metrics connection. It must be selected
    /// explicitly (<c>telemetry.policy = health-only</c>) and cannot carry metric parameters, so a
    /// metric-required profile can never silently degrade into it.
    /// </summary>
    public const string HealthOnlyPolicyName = "health-only";

    internal static readonly TimeSpan DefaultEvidenceGrace = TimeSpan.FromMinutes(15);
    internal static readonly TimeSpan DefaultMaximumEvidenceStaleness = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan DefaultExposureDeadline = TimeSpan.FromMinutes(30);

    private const double MaximumWarmupSeconds = 6 * 3600;
    private const double MaximumEvidenceGraceSeconds = 3600;
    private const double MaximumStalenessSeconds = 3600;
    private const double MaximumExposureDeadlineSeconds = 2 * 3600;
    private const int MaximumHealthProbeSamples = 20;
    private const int MaximumProbeTimeoutSeconds = 300;
    private const int MaximumConsecutiveBreaches = 100;

    /// <summary>
    /// Every operator-facing <c>telemetry.*</c> parameter the gate understands. Any other key under
    /// the prefix is rejected rather than silently ignored (honua-server#4617).
    /// </summary>
    private static readonly HashSet<string> RecognizedParameterKeys = new(StringComparer.Ordinal)
    {
        "telemetry.connection",
        "telemetry.policy",
        "telemetry.error_rate.query",
        "telemetry.error_rate.threshold",
        "telemetry.latency_p95.query",
        "telemetry.latency_p95.threshold_ms",
        "telemetry.sample_count.query",
        "telemetry.sample_count.minimum",
        "telemetry.warmup_seconds",
        "telemetry.evidence_grace_seconds",
        "telemetry.max_staleness_seconds",
        "telemetry.exposure_deadline_seconds",
        "telemetry.prometheus.selector",
        "telemetry.prometheus.job",
        "telemetry.prometheus.canary_selector",
        "telemetry.prometheus.canary_job",
        "telemetry.prometheus.extra_selector",
        "telemetry.healthz.url",
        "telemetry.healthz.failure_threshold",
        "telemetry.healthz.samples",
        "telemetry.healthz.expected_status",
        "telemetry.healthz.timeout_seconds",
        "telemetry.golden_query.url",
        "telemetry.golden_query.expected_sha256",
        "telemetry.golden_query.expected_contains",
        "telemetry.golden_query.forbidden_contains",
        "telemetry.golden_query.expected_status",
        "telemetry.golden_query.timeout_seconds",
        DeployTelemetrySignalEvaluator.BreachDebounceThresholdParameterKey,
        DeployTelemetrySignalEvaluator.InvalidPolicyGraceSecondsParameterKey,
        DeployTelemetrySignalEvaluator.BreachStreakParameterKey
    };

    private static readonly string[] MetricParameterPrefixes =
    [
        "telemetry.error_rate.",
        "telemetry.latency_p95.",
        "telemetry.sample_count.",
        "telemetry.prometheus.",
        "telemetry.max_staleness_seconds"
    ];

    public required string ConnectionId { get; init; }

    public string? ErrorRateQuery { get; init; }

    public double? ErrorRateThreshold { get; init; }

    public string? LatencyP95Query { get; init; }

    public double? LatencyP95ThresholdMs { get; init; }

    public string? MinimumSampleQuery { get; init; }

    public double? MinimumSampleCount { get; init; }

    public TimeSpan WarmupDuration { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How long missing/invalid telemetry evidence (provider outage, unconfigured connection,
    /// unsupported provider, ambiguous or stale samples, an unreachable health/golden-query probe)
    /// is tolerated after warmup before the gate escalates to a rollback recommendation
    /// (honua-server#4617). This is the declared bounded recovery policy after traffic exposure.
    /// Defaults to 15 minutes; <c>telemetry.evidence_grace_seconds</c> accepts (0, 3600].
    /// </summary>
    public TimeSpan EvidenceGraceDuration { get; init; } = DefaultEvidenceGrace;

    /// <summary>
    /// Maximum age of a queried telemetry sample (honua-server#4617). A provider-reported sample
    /// whose observation timestamp is further than this from now (or missing) is treated as absent
    /// and never satisfies a configured health requirement. Defaults to 5 minutes;
    /// <c>telemetry.max_staleness_seconds</c> accepts (0, 3600].
    /// </summary>
    public TimeSpan MaximumEvidenceStaleness { get; init; } = DefaultMaximumEvidenceStaleness;

    /// <summary>
    /// How long a submitted deploy may wait for the candidate to start receiving traffic before the
    /// gate fails it without activation (honua-server#4617). Before exposure no telemetry evidence
    /// is meaningful, so this bounds the pre-exposure hold. Defaults to 30 minutes;
    /// <c>telemetry.exposure_deadline_seconds</c> accepts (0, 7200].
    /// </summary>
    public TimeSpan ExposureDeadline { get; init; } = DefaultExposureDeadline;

    /// <summary>Indicates the explicit probe-only <see cref="HealthOnlyPolicyName"/> profile.</summary>
    public bool IsHealthOnly { get; init; }

    /// <summary>
    /// Indicates a preset whose synthesized queries select only the candidate (canary) revision's
    /// traffic, so its evidence identifies the candidate while traffic is split (honua-server#4617).
    /// </summary>
    public bool IsCandidateScoped { get; init; }

    /// <summary>
    /// Optional synthetic health-probe URL (typically <c>/healthz/ready</c>). When set, a failing
    /// probe during the bake window is a first-class rollback trigger inherited by every deploy
    /// backend and change class, independent of the metrics provider.
    /// </summary>
    public string? HealthProbeUrl { get; init; }

    /// <summary>Number of failing synthetic health checks (within a single scrape) that triggers rollback.</summary>
    public int HealthProbeFailureThreshold { get; init; } = 1;

    /// <summary>Number of sequential synthetic health checks issued per scrape.</summary>
    public int HealthProbeSamples { get; init; } = 3;

    /// <summary>HTTP status code a healthy synthetic health check returns.</summary>
    public int HealthProbeExpectedStatusCode { get; init; } = 200;

    /// <summary>Per-request timeout (seconds) for each synthetic health check.</summary>
    public int HealthProbeTimeoutSeconds { get; init; } = 5;

    /// <summary>Indicates a synthetic health-probe signal is configured.</summary>
    public bool HasHealthProbe => !string.IsNullOrWhiteSpace(HealthProbeUrl);

    /// <summary>
    /// Optional golden-query correctness endpoint. When set (with at least one expectation) a release whose
    /// status/5xx/p95 metrics are all healthy but whose golden-query response body is wrong or garbled is
    /// blocked from auto-promotion — the correctness gate beyond status/5xx/p95 (honua-server#2811). Opt-in
    /// and default off, so existing deploys are unaffected.
    /// </summary>
    public string? GoldenQueryUrl { get; init; }

    /// <summary>Expected lowercase hex SHA-256 of the golden-query response body.</summary>
    public string? GoldenQueryExpectedSha256 { get; init; }

    /// <summary>A substring the golden-query response body must contain.</summary>
    public string? GoldenQueryExpectedContains { get; init; }

    /// <summary>
    /// A wrong-result marker the golden-query response body must NOT contain (honua-server#4617), for
    /// example a sentinel the service emits when it serves a fallback or empty result.
    /// </summary>
    public string? GoldenQueryForbiddenContains { get; init; }

    /// <summary>HTTP status code the golden-query endpoint returns when servable.</summary>
    public int GoldenQueryExpectedStatusCode { get; init; } = 200;

    /// <summary>Per-request timeout (seconds) for the golden-query probe.</summary>
    public int GoldenQueryTimeoutSeconds { get; init; } = 5;

    /// <summary>
    /// Indicates a golden-query correctness gate is configured: a URL plus at least one body expectation
    /// (checksum or required substring). A URL with no expectation is a configuration error surfaced via
    /// <see cref="ValidationError"/> rather than a silently-ignored gate.
    /// </summary>
    public bool HasGoldenQuery =>
        !string.IsNullOrWhiteSpace(GoldenQueryUrl) &&
        (!string.IsNullOrWhiteSpace(GoldenQueryExpectedSha256) || !string.IsNullOrWhiteSpace(GoldenQueryExpectedContains));

    /// <summary>Indicates at least one queryable metric signal (error-rate / latency / sample-count) is configured.</summary>
    public bool HasMetricSignals =>
        !string.IsNullOrWhiteSpace(ErrorRateQuery) ||
        !string.IsNullOrWhiteSpace(LatencyP95Query) ||
        !string.IsNullOrWhiteSpace(MinimumSampleQuery);

    public string? ValidationError { get; init; }

    public bool IsValid => string.IsNullOrWhiteSpace(ValidationError);

    /// <summary>
    /// Indicates the operator supplied at least one explicit query override rather than relying
    /// on a Prometheus preset. Non-Prometheus providers require this because presets emit PromQL.
    /// </summary>
    public bool HasExplicitQueryOverride { get; init; }

    /// <summary>
    /// Projects this internal policy onto the provider-neutral descriptor passed to
    /// <see cref="IDeployTelemetryProviderEvaluator"/> implementations.
    /// </summary>
    public DeployTelemetryPolicyDescriptor ToDescriptor()
        => new()
        {
            ConnectionId = ConnectionId,
            ErrorRateQuery = ErrorRateQuery,
            ErrorRateThreshold = ErrorRateThreshold,
            LatencyP95Query = LatencyP95Query,
            LatencyP95ThresholdMs = LatencyP95ThresholdMs,
            MinimumSampleQuery = MinimumSampleQuery,
            MinimumSampleCount = MinimumSampleCount,
            HasExplicitQueryOverride = HasExplicitQueryOverride,
            MaximumEvidenceStaleness = MaximumEvidenceStaleness
        };

    /// <summary>
    /// Parses the telemetry gate from the deploy parameters. Returns <see langword="null"/> only when
    /// no <c>telemetry.*</c> parameter is configured at all. Any configured-but-unusable input
    /// (unknown key, malformed or out-of-range value, unsupported preset, missing connection, metric
    /// signals without a sample floor, a probe-only gate that was not declared health-only) yields a
    /// policy whose <see cref="ValidationError"/> names every problem, so the deploy is rejected at
    /// plan time instead of running with part of its configuration silently ignored (#4617).
    /// </summary>
    public static DeployTelemetryPolicy? Parse(DeployOperationSpec spec)
    {
        var parameters = spec.Parameters;
        var configuredKeys = parameters
            .Where(static parameter =>
                parameter.Key.StartsWith(TelemetryParameterPrefix, StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(parameter.Value) &&
                !string.Equals(parameter.Key, DeployTelemetrySignalEvaluator.BreachStreakParameterKey, StringComparison.Ordinal))
            .Select(static parameter => parameter.Key)
            .OrderBy(static key => key, StringComparer.Ordinal)
            .ToList();

        if (configuredKeys.Count == 0)
        {
            return null;
        }

        var errors = new List<string>();
        foreach (var key in configuredKeys.Where(static key => !RecognizedParameterKeys.Contains(key)))
        {
            errors.Add($"'{key}' is not a recognized deploy telemetry parameter.");
        }

        var connectionId = Get(parameters, "telemetry.connection");
        var policyName = Get(parameters, "telemetry.policy");
        var isHealthOnly = string.Equals(policyName, HealthOnlyPolicyName, StringComparison.OrdinalIgnoreCase);

        var warmupSeconds = ParseNumber(parameters, "telemetry.warmup_seconds", MaximumWarmupSeconds, errors);
        var evidenceGraceSeconds = ParseNumber(parameters, "telemetry.evidence_grace_seconds", MaximumEvidenceGraceSeconds, errors);
        var exposureDeadlineSeconds = ParseNumber(parameters, "telemetry.exposure_deadline_seconds", MaximumExposureDeadlineSeconds, errors);
        ParseNumber(parameters, DeployTelemetrySignalEvaluator.InvalidPolicyGraceSecondsParameterKey, MaximumEvidenceGraceSeconds, errors);
        ParseInteger(parameters, DeployTelemetrySignalEvaluator.BreachDebounceThresholdParameterKey, 1, MaximumConsecutiveBreaches, errors);

        // The synthetic health probe and the golden-query correctness gate are provider-independent
        // signals (#1849, #2811); both are evaluated alongside the metric gate or, under the explicit
        // health-only profile, instead of it.
        var healthProbeUrl = Get(parameters, "telemetry.healthz.url");
        var healthSamples = ParseInteger(parameters, "telemetry.healthz.samples", 1, MaximumHealthProbeSamples, errors) ?? 3;
        var healthFailureThreshold = ParseInteger(parameters, "telemetry.healthz.failure_threshold", 1, MaximumHealthProbeSamples, errors) ?? 1;
        var healthExpectedStatus = ParseInteger(parameters, "telemetry.healthz.expected_status", 100, 599, errors) ?? 200;
        var healthTimeoutSeconds = ParseInteger(parameters, "telemetry.healthz.timeout_seconds", 1, MaximumProbeTimeoutSeconds, errors) ?? 5;
        if (healthFailureThreshold > healthSamples)
        {
            errors.Add(
                $"telemetry.healthz.failure_threshold ({healthFailureThreshold}) exceeds telemetry.healthz.samples ({healthSamples}), " +
                "so the health probe could never fail.");
        }

        var goldenQueryUrl = Get(parameters, "telemetry.golden_query.url");
        var goldenQuerySha256 = Get(parameters, "telemetry.golden_query.expected_sha256");
        var goldenQueryContains = Get(parameters, "telemetry.golden_query.expected_contains");
        var goldenQueryForbidden = Get(parameters, "telemetry.golden_query.forbidden_contains");
        var goldenQueryExpectedStatus = ParseInteger(parameters, "telemetry.golden_query.expected_status", 100, 599, errors) ?? 200;
        var goldenQueryTimeoutSeconds = ParseInteger(parameters, "telemetry.golden_query.timeout_seconds", 1, MaximumProbeTimeoutSeconds, errors) ?? 5;
        if (!string.IsNullOrWhiteSpace(goldenQueryUrl) &&
            string.IsNullOrWhiteSpace(goldenQuerySha256) &&
            string.IsNullOrWhiteSpace(goldenQueryContains))
        {
            errors.Add("Deploy telemetry policy is invalid because telemetry.golden_query.url is set " +
                "without telemetry.golden_query.expected_sha256 or telemetry.golden_query.expected_contains.");
        }

        if (string.IsNullOrWhiteSpace(goldenQueryUrl) &&
            (goldenQuerySha256 != null || goldenQueryContains != null || goldenQueryForbidden != null))
        {
            errors.Add("telemetry.golden_query expectations are set without telemetry.golden_query.url, so they would never be checked.");
        }

        if (string.IsNullOrWhiteSpace(healthProbeUrl) &&
            configuredKeys.Any(static key => key.StartsWith("telemetry.healthz.", StringComparison.Ordinal)))
        {
            errors.Add("telemetry.healthz settings are set without telemetry.healthz.url, so they would never be applied.");
        }

        string? errorRateQuery = null;
        string? latencyQuery = null;
        string? sampleQuery = null;
        double? errorThreshold = null;
        double? latencyThreshold = null;
        double? sampleMinimum = null;
        double? maxStalenessSeconds = null;
        var hasExplicitQueryOverride = false;
        DeployTelemetryPolicy? preset = null;

        if (isHealthOnly)
        {
            if (connectionId != null)
            {
                errors.Add($"telemetry.policy '{HealthOnlyPolicyName}' does not use a metrics connection; remove telemetry.connection " +
                    "or choose a metrics profile so the connection is not silently ignored.");
            }

            var metricKeys = configuredKeys
                .Where(static key => MetricParameterPrefixes.Any(prefix => key.StartsWith(prefix, StringComparison.Ordinal)))
                .ToList();
            if (metricKeys.Count > 0)
            {
                errors.Add($"telemetry.policy '{HealthOnlyPolicyName}' cannot carry metric parameters ({string.Join(", ", metricKeys)}); " +
                    "use a metrics profile with telemetry.connection instead.");
            }

            if (string.IsNullOrWhiteSpace(healthProbeUrl) && string.IsNullOrWhiteSpace(goldenQueryUrl))
            {
                errors.Add($"telemetry.policy '{HealthOnlyPolicyName}' requires telemetry.healthz.url or telemetry.golden_query.url.");
            }
        }
        else
        {
            if (connectionId == null)
            {
                errors.Add("Deploy telemetry policy is invalid because telemetry.connection is missing: the configured " +
                    $"telemetry parameters ({string.Join(", ", configuredKeys)}) would be silently ignored. " +
                    $"Set telemetry.connection, or telemetry.policy '{HealthOnlyPolicyName}' for a probe-only gate.");
            }

            var presetResolution = ResolvePreset(spec, policyName);
            preset = presetResolution.Preset;
            if (presetResolution.Error != null)
            {
                errors.Add(presetResolution.Error);
            }

            var explicitErrorQuery = Get(parameters, "telemetry.error_rate.query");
            var explicitLatencyQuery = Get(parameters, "telemetry.latency_p95.query");
            var explicitSampleQuery = Get(parameters, "telemetry.sample_count.query");
            errorRateQuery = explicitErrorQuery ?? preset?.ErrorRateQuery;
            latencyQuery = explicitLatencyQuery ?? preset?.LatencyP95Query;
            sampleQuery = explicitSampleQuery ?? preset?.MinimumSampleQuery;

            // A malformed explicit threshold is reported by ParseNumber and must not be papered over
            // by the preset default; only an absent key inherits the preset value.
            var hasErrorThreshold = Get(parameters, "telemetry.error_rate.threshold") != null;
            var hasLatencyThreshold = Get(parameters, "telemetry.latency_p95.threshold_ms") != null;
            var hasSampleMinimum = Get(parameters, "telemetry.sample_count.minimum") != null;
            errorThreshold = hasErrorThreshold
                ? ParseNumber(parameters, "telemetry.error_rate.threshold", double.MaxValue, errors, allowZero: true)
                : preset?.ErrorRateThreshold;
            latencyThreshold = hasLatencyThreshold
                ? ParseNumber(parameters, "telemetry.latency_p95.threshold_ms", double.MaxValue, errors)
                : preset?.LatencyP95ThresholdMs;
            sampleMinimum = hasSampleMinimum
                ? ParseNumber(parameters, "telemetry.sample_count.minimum", double.MaxValue, errors)
                : preset?.MinimumSampleCount;
            maxStalenessSeconds = ParseNumber(parameters, "telemetry.max_staleness_seconds", MaximumStalenessSeconds, errors);

            // When the operator supplied explicit query overrides, the preset's input
            // requirement (e.g. canary selector / job) no longer applies — the per-query
            // threshold checks below validate the override-only policy.
            hasExplicitQueryOverride =
                !string.IsNullOrWhiteSpace(explicitErrorQuery) ||
                !string.IsNullOrWhiteSpace(explicitLatencyQuery) ||
                !string.IsNullOrWhiteSpace(explicitSampleQuery);
            if (!hasExplicitQueryOverride && preset?.ValidationError is { } presetError)
            {
                errors.Add(presetError);
            }

            // Target/revision identity (#4617): while traffic is split between the stable and candidate
            // revisions, aggregate metrics blend the stable revision's healthy traffic into the
            // candidate's evidence, so a degraded canary can read as healthy. A split rollout must read
            // candidate-scoped signals. Without a split, all post-exposure traffic is the candidate's.
            if (SplitsTraffic(parameters) &&
                !hasExplicitQueryOverride &&
                preset is { IsValid: true, IsCandidateScoped: false })
            {
                errors.Add("Deploy telemetry policy is invalid because the rollout splits traffic (canary weight or ramp) but its " +
                    "preset metrics are aggregate and cannot identify the candidate revision; set telemetry.prometheus.canary_selector " +
                    "or telemetry.prometheus.canary_job, a canary preset, or explicit candidate-scoped queries.");
            }

            if (!string.IsNullOrWhiteSpace(errorRateQuery) && !errorThreshold.HasValue && !hasErrorThreshold)
            {
                errors.Add("Deploy telemetry policy is invalid because telemetry.error_rate.threshold is missing.");
            }

            if (!string.IsNullOrWhiteSpace(latencyQuery) && !latencyThreshold.HasValue && !hasLatencyThreshold)
            {
                errors.Add("Deploy telemetry policy is invalid because telemetry.latency_p95.threshold_ms is missing.");
            }

            if (!string.IsNullOrWhiteSpace(sampleQuery) && !sampleMinimum.HasValue && !hasSampleMinimum)
            {
                errors.Add("Deploy telemetry policy is invalid because telemetry.sample_count.minimum is missing.");
            }

            // A sample floor is what distinguishes "healthy" from "no traffic yet": without one, a
            // zero-traffic error ratio or an empty latency histogram can satisfy the gate (#4617).
            if ((!string.IsNullOrWhiteSpace(errorRateQuery) || !string.IsNullOrWhiteSpace(latencyQuery)) &&
                string.IsNullOrWhiteSpace(sampleQuery))
            {
                errors.Add("Deploy telemetry policy is invalid because error-rate/latency signals require a sample floor " +
                    "(telemetry.sample_count.query and telemetry.sample_count.minimum) so insufficient traffic can never satisfy the gate.");
            }

            if (connectionId != null &&
                string.IsNullOrWhiteSpace(errorRateQuery) &&
                string.IsNullOrWhiteSpace(latencyQuery) &&
                string.IsNullOrWhiteSpace(sampleQuery) &&
                presetResolution.Error == null)
            {
                errors.Add($"Deploy telemetry connection '{connectionId}' is configured but no metric signal is defined; " +
                    $"configure metric queries or a preset, or use telemetry.policy '{HealthOnlyPolicyName}' for a probe-only gate.");
            }
        }

        return new DeployTelemetryPolicy
        {
            ConnectionId = connectionId ?? string.Empty,
            IsHealthOnly = isHealthOnly,
            // Only preset-synthesized queries are known to be candidate-scoped; an explicit override
            // replaces them with operator queries whose scope the parser cannot verify.
            IsCandidateScoped = preset?.IsCandidateScoped == true && !hasExplicitQueryOverride,
            ErrorRateQuery = errorRateQuery,
            ErrorRateThreshold = errorThreshold,
            LatencyP95Query = latencyQuery,
            LatencyP95ThresholdMs = latencyThreshold,
            MinimumSampleQuery = sampleQuery,
            MinimumSampleCount = sampleMinimum,
            WarmupDuration = warmupSeconds.HasValue
                ? TimeSpan.FromSeconds(warmupSeconds.Value)
                : preset?.WarmupDuration ?? TimeSpan.FromMinutes(2),
            EvidenceGraceDuration = evidenceGraceSeconds.HasValue
                ? TimeSpan.FromSeconds(evidenceGraceSeconds.Value)
                : DefaultEvidenceGrace,
            MaximumEvidenceStaleness = maxStalenessSeconds.HasValue
                ? TimeSpan.FromSeconds(maxStalenessSeconds.Value)
                : DefaultMaximumEvidenceStaleness,
            ExposureDeadline = exposureDeadlineSeconds.HasValue
                ? TimeSpan.FromSeconds(exposureDeadlineSeconds.Value)
                : DefaultExposureDeadline,
            HealthProbeUrl = healthProbeUrl,
            HealthProbeFailureThreshold = healthFailureThreshold,
            HealthProbeSamples = healthSamples,
            HealthProbeExpectedStatusCode = healthExpectedStatus,
            HealthProbeTimeoutSeconds = healthTimeoutSeconds,
            GoldenQueryUrl = goldenQueryUrl,
            GoldenQueryExpectedSha256 = goldenQuerySha256,
            GoldenQueryExpectedContains = goldenQueryContains,
            GoldenQueryForbiddenContains = goldenQueryForbidden,
            GoldenQueryExpectedStatusCode = goldenQueryExpectedStatus,
            GoldenQueryTimeoutSeconds = goldenQueryTimeoutSeconds,
            ValidationError = errors.Count == 0 ? null : string.Join(" ", errors),
            HasExplicitQueryOverride = hasExplicitQueryOverride
        };
    }

    private static (DeployTelemetryPolicy? Preset, string? Error) ResolvePreset(DeployOperationSpec spec, string? explicitPolicyName)
    {
        var parameters = spec.Parameters;
        var policyName = explicitPolicyName ?? GetDefaultPolicyName(spec);
        if (string.IsNullOrWhiteSpace(policyName))
        {
            return (null, null);
        }

        return policyName.ToLowerInvariant() switch
        {
            "honua-http" or "kubernetes-honua-http" => (CreateHonuaHttpPreset(parameters), null),
            "kubernetes-canary" => (CreateCanaryPreset(parameters, "kubernetes-canary"), null),
            GpBatchPolicyName or "serverless-gp" => (CreateGpBatchPreset(parameters), null),
            "aws-alb-canary" => (CreateAwsAlbCanaryPreset(parameters), null),
            "aws-lambda-canary" => (CreateAwsLambdaCanaryPreset(parameters), null),
            "azure-aca-canary" => (CreateAzureAcaCanaryPreset(parameters), null),
            // An unsupported preset is rejected even alongside explicit query overrides: silently
            // ignoring the name the operator asked for is exactly the #4617 failure mode.
            _ => (null, $"Deploy telemetry policy '{policyName}' is not supported.")
        };
    }

    private static string? GetDefaultPolicyName(DeployOperationSpec spec)
        => spec.TargetKind switch
        {
            // A canary selector/job means the rollout reads candidate-only traffic (#4617), exactly as
            // for the cloud canary targets below.
            DeployTargetKind.Kubernetes => HasCanarySignalConfiguration(spec.Parameters) ? "kubernetes-canary" : "kubernetes-honua-http",
            // ECS canary deploys are configured by setting a canary weight via
            // aws.ecs.canary_weight_percentage or the generic
            // deployment.canary_weight_percentage; either key implies the rollout
            // is gated on canary-only telemetry. Without that signal the runbook
            // and PlanAsync would advertise a canary policy while the evaluator
            // silently fell back to aggregate Honua HTTP metrics.
            DeployTargetKind.AwsEcs => HasCanarySignalConfiguration(spec.Parameters) || HasCanaryWeight(spec.Parameters)
                ? "aws-alb-canary"
                : "honua-http",
            DeployTargetKind.AwsLambda => HasCanarySignalConfiguration(spec.Parameters) ? "aws-lambda-canary" : "honua-http",
            DeployTargetKind.AzureContainerApps => HasCanarySignalConfiguration(spec.Parameters) ? "azure-aca-canary" : "honua-http",
            DeployTargetKind.AzureFunctions => "honua-http",
            // Self-hosted rolling replace maps to the "honua-http" preset so a configured metrics
            // connection is gated like any other target (ADR-0060). A probe-only self-hosted gate
            // (no metrics substrate on-prem/air-gapped) must select telemetry.policy = health-only
            // explicitly; the backend's own standby health gate drives promotion either way.
            DeployTargetKind.SelfHostedRolling => "honua-http",
            _ => null
        };

    private static bool HasCanarySignalConfiguration(IReadOnlyDictionary<string, string> parameters)
        => parameters.ContainsKey("telemetry.prometheus.canary_selector")
           || parameters.ContainsKey("telemetry.prometheus.canary_job");

    private static bool HasCanaryWeight(IReadOnlyDictionary<string, string> parameters)
        => HasNonEmpty(parameters, "aws.ecs.canary_weight_percentage")
           || HasNonEmpty(parameters, "deployment.canary_weight_percentage");

    private static bool SplitsTraffic(IReadOnlyDictionary<string, string> parameters)
        => HasCanaryWeight(parameters) || HasNonEmpty(parameters, "deployment.canary_ramp.step_weights");

    private static bool HasNonEmpty(IReadOnlyDictionary<string, string> parameters, string key)
        => parameters.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value);

    private static DeployTelemetryPolicy CreateHonuaHttpPreset(IReadOnlyDictionary<string, string> parameters)
    {
        var selector = BuildPrometheusSelector(
            parameters,
            selectorKey: "telemetry.prometheus.selector",
            jobKey: "telemetry.prometheus.job",
            defaultJob: DefaultPrometheusJob);

        return string.IsNullOrWhiteSpace(selector)
            ? InvalidPreset("Deploy telemetry policy 'kubernetes-honua-http' requires a Prometheus selector or job.")
            : CreateHonuaHttpPolicy(selector, warmupDuration: TimeSpan.FromMinutes(2));
    }

    private static DeployTelemetryPolicy CreateGpBatchPreset(IReadOnlyDictionary<string, string> parameters)
    {
        var selector = BuildPrometheusSelector(
            parameters,
            selectorKey: "telemetry.prometheus.selector",
            jobKey: "telemetry.prometheus.job",
            defaultJob: DefaultPrometheusJob);

        // Longer bake (5m) and a low sample floor (10) suit GP's sparse/bursty per-job metrics;
        // operators typically override the synthesized PromQL with GP-specific queries.
        return string.IsNullOrWhiteSpace(selector)
            ? InvalidPreset($"Deploy telemetry policy '{GpBatchPolicyName}' requires a Prometheus selector or job.")
            : CreateHonuaHttpPolicy(selector, warmupDuration: TimeSpan.FromMinutes(5), minimumSampleCount: 10);
    }

    private static DeployTelemetryPolicy CreateAwsAlbCanaryPreset(IReadOnlyDictionary<string, string> parameters)
        => CreateCanaryPreset(parameters, "aws-alb-canary");

    private static DeployTelemetryPolicy CreateAwsLambdaCanaryPreset(IReadOnlyDictionary<string, string> parameters)
        => CreateCanaryPreset(parameters, "aws-lambda-canary");

    private static DeployTelemetryPolicy CreateAzureAcaCanaryPreset(IReadOnlyDictionary<string, string> parameters)
        => CreateCanaryPreset(parameters, "azure-aca-canary");

    private static DeployTelemetryPolicy CreateCanaryPreset(IReadOnlyDictionary<string, string> parameters, string presetName)
    {
        var selector = BuildPrometheusSelector(
            parameters,
            selectorKey: "telemetry.prometheus.canary_selector",
            jobKey: "telemetry.prometheus.canary_job",
            defaultJob: DefaultCanaryPrometheusJob,
            fallbackSelectorKey: "telemetry.prometheus.selector",
            fallbackJobKey: "telemetry.prometheus.job");

        return string.IsNullOrWhiteSpace(selector)
            ? InvalidPreset($"Deploy telemetry policy '{presetName}' requires a canary Prometheus selector or canary job.")
            : CreateHonuaHttpPolicy(selector, warmupDuration: TimeSpan.FromMinutes(3), minimumSampleCount: 10) with { IsCandidateScoped = true };
    }

    private static DeployTelemetryPolicy CreateHonuaHttpPolicy(
        string selector,
        TimeSpan warmupDuration,
        double minimumSampleCount = 20)
    {
        var metricSelector = WrapSelector(selector);
        var errorSelector = AppendLabelMatcher(selector, "status_code=~\"5..\"");

        return new DeployTelemetryPolicy
        {
            ConnectionId = string.Empty,
            ErrorRateQuery =
                $"sum(rate(honua_http_request_total{WrapSelector(errorSelector)}[5m])) / clamp_min(sum(rate(honua_http_request_total{metricSelector}[5m])), 0.001)",
            ErrorRateThreshold = 0.05,
            LatencyP95Query =
                $"histogram_quantile(0.95, sum(rate(honua_http_request_duration_ms_bucket{metricSelector}[5m])) by (le))",
            LatencyP95ThresholdMs = 2000,
            MinimumSampleQuery =
                $"sum(rate(honua_http_request_total{metricSelector}[5m])) * 300",
            MinimumSampleCount = minimumSampleCount,
            WarmupDuration = warmupDuration
        };
    }

    private static DeployTelemetryPolicy InvalidPreset(string message)
        => new()
        {
            ConnectionId = string.Empty,
            ValidationError = message
        };

    private static string BuildPrometheusSelector(
        IReadOnlyDictionary<string, string> parameters,
        string selectorKey,
        string jobKey,
        string? defaultJob,
        string? fallbackSelectorKey = null,
        string? fallbackJobKey = null)
    {
        var rawSelector = Get(parameters, selectorKey)
            ?? (fallbackSelectorKey != null ? Get(parameters, fallbackSelectorKey) : null);
        var extraSelector = Get(parameters, "telemetry.prometheus.extra_selector");
        var job = Get(parameters, jobKey)
            ?? (fallbackJobKey != null ? Get(parameters, fallbackJobKey) : null)
            ?? defaultJob;

        var matchers = new List<string>();
        if (!string.IsNullOrWhiteSpace(rawSelector))
        {
            matchers.Add(rawSelector);
        }
        else if (!string.IsNullOrWhiteSpace(job))
        {
            matchers.Add($"job={QuotePrometheusValue(job)}");
        }

        if (!string.IsNullOrWhiteSpace(extraSelector))
        {
            matchers.Add(extraSelector);
        }

        return string.Join(",", matchers.Where(static matcher => !string.IsNullOrWhiteSpace(matcher)));
    }

    private static string QuotePrometheusValue(string value)
        => $"\"{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

    private static string WrapSelector(string selector)
        => string.IsNullOrWhiteSpace(selector) ? string.Empty : $"{{{selector}}}";

    private static string AppendLabelMatcher(string selector, string labelMatcher)
        => string.IsNullOrWhiteSpace(selector) ? labelMatcher : $"{selector},{labelMatcher}";

    private static string? Get(IReadOnlyDictionary<string, string> parameters, string key)
        => parameters.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;

    /// <summary>
    /// Reads an optional finite number in <c>(0, maximum]</c> (or <c>[0, maximum]</c> when
    /// <paramref name="allowZero"/>). A present-but-unusable value is recorded in
    /// <paramref name="errors"/> and returns <see langword="null"/>; it is never replaced by a default.
    /// </summary>
    private static double? ParseNumber(
        IReadOnlyDictionary<string, string> parameters,
        string key,
        double maximum,
        List<string> errors,
        bool allowZero = false)
    {
        if (!parameters.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (double.TryParse(raw, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var parsed) &&
            double.IsFinite(parsed) &&
            (allowZero ? parsed >= 0 : parsed > 0) &&
            parsed <= maximum)
        {
            return parsed;
        }

        var range = maximum == double.MaxValue
            ? allowZero ? "a finite number >= 0" : "a finite number > 0"
            : allowZero
                ? $"a finite number in [0, {maximum.ToString(CultureInfo.InvariantCulture)}]"
                : $"a finite number in (0, {maximum.ToString(CultureInfo.InvariantCulture)}]";
        errors.Add($"{key} must be {range} (got '{raw.Trim()}').");
        return null;
    }

    /// <summary>
    /// Reads an optional integer in <c>[minimum, maximum]</c>. A present-but-unusable value is
    /// recorded in <paramref name="errors"/> and returns <see langword="null"/>.
    /// </summary>
    private static int? ParseInteger(
        IReadOnlyDictionary<string, string> parameters,
        string key,
        int minimum,
        int maximum,
        List<string> errors)
    {
        if (!parameters.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) &&
            parsed >= minimum &&
            parsed <= maximum)
        {
            return parsed;
        }

        errors.Add($"{key} must be an integer in [{minimum}, {maximum}] (got '{raw.Trim()}').");
        return null;
    }
}
