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
    private const string DefaultPrometheusJob = "honua";
    private const string DefaultCanaryPrometheusJob = "honua-canary";

    public required string ConnectionId { get; init; }

    public string? ErrorRateQuery { get; init; }

    public double? ErrorRateThreshold { get; init; }

    public string? LatencyP95Query { get; init; }

    public double? LatencyP95ThresholdMs { get; init; }

    public string? MinimumSampleQuery { get; init; }

    public double? MinimumSampleCount { get; init; }

    public TimeSpan WarmupDuration { get; init; } = TimeSpan.FromMinutes(2);

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
            HasExplicitQueryOverride = HasExplicitQueryOverride
        };

    public static DeployTelemetryPolicy? Parse(DeployOperationSpec spec)
    {
        var parameters = spec.Parameters;
        if (!parameters.TryGetValue("telemetry.connection", out var connectionId) ||
            string.IsNullOrWhiteSpace(connectionId))
        {
            return null;
        }

        var preset = ResolvePreset(spec);
        var explicitErrorQuery = Get(parameters, "telemetry.error_rate.query");
        var explicitLatencyQuery = Get(parameters, "telemetry.latency_p95.query");
        var explicitSampleQuery = Get(parameters, "telemetry.sample_count.query");
        var errorRateQuery = explicitErrorQuery ?? preset?.ErrorRateQuery;
        var latencyQuery = explicitLatencyQuery ?? preset?.LatencyP95Query;
        var sampleQuery = explicitSampleQuery ?? preset?.MinimumSampleQuery;

        if (string.IsNullOrWhiteSpace(errorRateQuery) &&
            string.IsNullOrWhiteSpace(latencyQuery) &&
            string.IsNullOrWhiteSpace(sampleQuery))
        {
            return null;
        }

        var errorThreshold = ParseOptionalDouble(parameters, "telemetry.error_rate.threshold") ?? preset?.ErrorRateThreshold;
        var latencyThreshold = ParseOptionalDouble(parameters, "telemetry.latency_p95.threshold_ms") ?? preset?.LatencyP95ThresholdMs;
        var sampleMinimum = ParseOptionalDouble(parameters, "telemetry.sample_count.minimum") ?? preset?.MinimumSampleCount;
        var warmupSeconds = ParseOptionalDouble(parameters, "telemetry.warmup_seconds");

        // When the operator supplied explicit query overrides, the preset's input
        // requirement (e.g. canary selector / job) no longer applies — the per-query
        // threshold checks below validate the override-only policy.
        var hasExplicitQueryOverride =
            !string.IsNullOrWhiteSpace(explicitErrorQuery) ||
            !string.IsNullOrWhiteSpace(explicitLatencyQuery) ||
            !string.IsNullOrWhiteSpace(explicitSampleQuery);
        var validationError = hasExplicitQueryOverride ? null : preset?.ValidationError;
        if (!string.IsNullOrWhiteSpace(errorRateQuery) && !errorThreshold.HasValue)
        {
            validationError = "Deploy telemetry policy is invalid because telemetry.error_rate.threshold is missing.";
        }
        else if (!string.IsNullOrWhiteSpace(latencyQuery) && !latencyThreshold.HasValue)
        {
            validationError = "Deploy telemetry policy is invalid because telemetry.latency_p95.threshold_ms is missing.";
        }
        else if (!string.IsNullOrWhiteSpace(sampleQuery) && !sampleMinimum.HasValue)
        {
            validationError = "Deploy telemetry policy is invalid because telemetry.sample_count.minimum is missing.";
        }

        return new DeployTelemetryPolicy
        {
            ConnectionId = connectionId.Trim(),
            ErrorRateQuery = errorRateQuery,
            ErrorRateThreshold = errorThreshold,
            LatencyP95Query = latencyQuery,
            LatencyP95ThresholdMs = latencyThreshold,
            MinimumSampleQuery = sampleQuery,
            MinimumSampleCount = sampleMinimum,
            WarmupDuration = warmupSeconds.HasValue && warmupSeconds.Value > 0
                ? TimeSpan.FromSeconds(warmupSeconds.Value)
                : preset?.WarmupDuration ?? TimeSpan.FromMinutes(2),
            ValidationError = validationError,
            HasExplicitQueryOverride = hasExplicitQueryOverride
        };
    }

    private static DeployTelemetryPolicy? ResolvePreset(DeployOperationSpec spec)
    {
        var parameters = spec.Parameters;
        var policyName = Get(parameters, "telemetry.policy") ?? GetDefaultPolicyName(spec);
        if (string.IsNullOrWhiteSpace(policyName))
        {
            return null;
        }

        return policyName.ToLowerInvariant() switch
        {
            "honua-http" or "kubernetes-honua-http" => CreateHonuaHttpPreset(parameters),
            "aws-alb-canary" => CreateAwsAlbCanaryPreset(parameters),
            "aws-lambda-canary" => CreateAwsLambdaCanaryPreset(parameters),
            "azure-aca-canary" => CreateAzureAcaCanaryPreset(parameters),
            _ => new DeployTelemetryPolicy
            {
                ConnectionId = string.Empty,
                ValidationError = $"Deploy telemetry policy '{policyName}' is not supported."
            }
        };
    }

    private static string? GetDefaultPolicyName(DeployOperationSpec spec)
        => spec.TargetKind switch
        {
            DeployTargetKind.Kubernetes => "kubernetes-honua-http",
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
            _ => null
        };

    private static bool HasCanarySignalConfiguration(IReadOnlyDictionary<string, string> parameters)
        => parameters.ContainsKey("telemetry.prometheus.canary_selector")
           || parameters.ContainsKey("telemetry.prometheus.canary_job");

    private static bool HasCanaryWeight(IReadOnlyDictionary<string, string> parameters)
        => HasNonEmpty(parameters, "aws.ecs.canary_weight_percentage")
           || HasNonEmpty(parameters, "deployment.canary_weight_percentage");

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
            : CreateHonuaHttpPolicy(selector, warmupDuration: TimeSpan.FromMinutes(3), minimumSampleCount: 10);
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

    private static double? ParseOptionalDouble(IReadOnlyDictionary<string, string> parameters, string key)
    {
        if (!parameters.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return double.TryParse(raw, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }
}
