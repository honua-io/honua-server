// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Honua.Core.Configuration;
using Honua.Core.Features.ControlPlane.Domain;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Infrastructure.ControlPlane;

/// <summary>
/// Evaluates live deploy telemetry signals against a queryable backend.
/// </summary>
internal interface IDeployTelemetrySignalEvaluator
{
    Task<DeployTelemetryDecision?> EvaluateAsync(
        WorkflowOperationRecord operation,
        CancellationToken cancellationToken = default);
}

internal sealed record DeployTelemetryDecision
{
    public bool WaitForMoreTelemetry { get; init; }

    public bool RollbackRecommended { get; init; }

    public string Message { get; init; } = string.Empty;
}

/// <summary>
/// Prometheus-compatible telemetry evaluator used to gate deploy success and trigger rollback.
/// </summary>
internal sealed class PrometheusDeployTelemetrySignalEvaluator(
    IOptionsMonitor<ControlPlaneOptions> optionsMonitor,
    IHttpClientFactory httpClientFactory,
    ILogger<PrometheusDeployTelemetrySignalEvaluator> logger) : IDeployTelemetrySignalEvaluator
{
    public async Task<DeployTelemetryDecision?> EvaluateAsync(
        WorkflowOperationRecord operation,
        CancellationToken cancellationToken = default)
    {
        if (operation.Deploy == null)
        {
            return null;
        }

        var policy = DeployTelemetryPolicy.Parse(operation.Deploy);
        if (policy == null)
        {
            return null;
        }

        if (!policy.IsValid)
        {
            return new DeployTelemetryDecision
            {
                WaitForMoreTelemetry = true,
                Message = policy.ValidationError ?? "Deploy telemetry policy is invalid."
            };
        }

        if (DateTimeOffset.UtcNow - operation.CreatedAt < policy.WarmupDuration)
        {
            var remaining = policy.WarmupDuration - (DateTimeOffset.UtcNow - operation.CreatedAt);
            return new DeployTelemetryDecision
            {
                WaitForMoreTelemetry = true,
                Message = $"Waiting for telemetry warmup to complete before settling deploy ({Math.Ceiling(Math.Max(remaining.TotalSeconds, 0))}s remaining)."
            };
        }

        var connection = optionsMonitor.CurrentValue.TelemetryConnections
            .FirstOrDefault(candidate => string.Equals(candidate.ConnectionId, policy.ConnectionId, StringComparison.Ordinal));

        if (connection == null)
        {
            return new DeployTelemetryDecision
            {
                WaitForMoreTelemetry = true,
                Message = $"Waiting for telemetry confirmation because connection '{policy.ConnectionId}' is not configured."
            };
        }

        if (!string.Equals(connection.Provider, "prometheus", StringComparison.OrdinalIgnoreCase))
        {
            return new DeployTelemetryDecision
            {
                WaitForMoreTelemetry = true,
                Message = $"Waiting for telemetry confirmation because provider '{connection.Provider}' is not supported for deploy rollback signals."
            };
        }

        try
        {
            var validatedConnection = await ValidateConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
            var sampleCount = string.IsNullOrWhiteSpace(policy.MinimumSampleQuery)
                ? null
                : await ExecutePrometheusQueryAsync(validatedConnection, policy.MinimumSampleQuery, cancellationToken).ConfigureAwait(false);

            if (policy.MinimumSampleCount.HasValue && (!sampleCount.HasValue || sampleCount.Value < policy.MinimumSampleCount.Value))
            {
                return new DeployTelemetryDecision
                {
                    WaitForMoreTelemetry = true,
                    Message = sampleCount.HasValue
                        ? $"Waiting for telemetry confirmation because sample count {sampleCount.Value.ToString("0.###", CultureInfo.InvariantCulture)} is below the required minimum {policy.MinimumSampleCount.Value.ToString("0.###", CultureInfo.InvariantCulture)}."
                        : "Waiting for telemetry confirmation because no sample-count signal is available yet."
                };
            }

            var breachMessages = new List<string>();
            var healthySignals = new List<string>();

            if (!string.IsNullOrWhiteSpace(policy.ErrorRateQuery) && policy.ErrorRateThreshold.HasValue)
            {
                var errorRate = await ExecutePrometheusQueryAsync(validatedConnection, policy.ErrorRateQuery, cancellationToken).ConfigureAwait(false);
                if (errorRate.HasValue && errorRate.Value > policy.ErrorRateThreshold.Value)
                {
                    breachMessages.Add(
                        $"error rate {errorRate.Value.ToString("0.###", CultureInfo.InvariantCulture)} exceeded threshold {policy.ErrorRateThreshold.Value.ToString("0.###", CultureInfo.InvariantCulture)}");
                }
                else
                {
                    healthySignals.Add("error-rate signal is within threshold");
                }
            }

            if (!string.IsNullOrWhiteSpace(policy.LatencyP95Query) && policy.LatencyP95ThresholdMs.HasValue)
            {
                var latencyP95 = await ExecutePrometheusQueryAsync(validatedConnection, policy.LatencyP95Query, cancellationToken).ConfigureAwait(false);
                if (latencyP95.HasValue && latencyP95.Value > policy.LatencyP95ThresholdMs.Value)
                {
                    breachMessages.Add(
                        $"p95 latency {latencyP95.Value.ToString("0.###", CultureInfo.InvariantCulture)}ms exceeded threshold {policy.LatencyP95ThresholdMs.Value.ToString("0.###", CultureInfo.InvariantCulture)}ms");
                }
                else
                {
                    healthySignals.Add("latency signal is within threshold");
                }
            }

            if (breachMessages.Count > 0)
            {
                return new DeployTelemetryDecision
                {
                    RollbackRecommended = true,
                    Message = $"Automatic rollback requested because telemetry detected canary degradation: {string.Join("; ", breachMessages)}."
                };
            }

            return new DeployTelemetryDecision
            {
                Message = healthySignals.Count > 0
                    ? $"Telemetry gate passed: {string.Join("; ", healthySignals)}."
                    : "Telemetry gate passed."
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to evaluate deploy telemetry signals for operation {OperationId}", operation.OperationId);
            return new DeployTelemetryDecision
            {
                WaitForMoreTelemetry = true,
                Message = "Waiting for telemetry confirmation because the telemetry query backend is unavailable."
            };
        }
    }

    private static async Task<ValidatedTelemetryConnection> ValidateConnectionAsync(
        DeployTelemetryConnectionOptions connection,
        CancellationToken cancellationToken)
    {
        var baseUrlValidation = await OutboundHttpUrlValidator
            .ValidateAsync(connection.BaseUrl, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!baseUrlValidation.IsValid || baseUrlValidation.Uri is null)
        {
            throw new InvalidOperationException(
                $"Telemetry connection '{connection.ConnectionId}' base URL {baseUrlValidation.ErrorMessage ?? "must be a valid HTTPS URL."}");
        }

        if (!ControlPlaneTelemetryConnectionValidation.TryNormalizeQueryPath(
                connection.QueryPath,
                out var queryPath,
                out var queryPathError))
        {
            throw new InvalidOperationException(
                $"Telemetry connection '{connection.ConnectionId}' query path {queryPathError}");
        }

        if (!ControlPlaneTelemetryConnectionValidation.TryNormalizeAuthHeader(
                connection.AuthHeaderName,
                connection.AuthHeaderValue,
                out var authHeaderName,
                out var authHeaderValue,
                out var authHeaderError))
        {
            throw new InvalidOperationException(
                $"Telemetry connection '{connection.ConnectionId}' {authHeaderError}");
        }

        return new ValidatedTelemetryConnection(
            baseUrlValidation.Uri,
            queryPath,
            authHeaderName,
            authHeaderValue,
            Math.Max(1, connection.TimeoutSeconds));
    }

    private async Task<double?> ExecutePrometheusQueryAsync(
        ValidatedTelemetryConnection connection,
        string query,
        CancellationToken cancellationToken)
    {
        var requestUri = new Uri(connection.BaseUri, $"{connection.QueryPath}?query={Uri.EscapeDataString(query)}");
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);

        if (!string.IsNullOrWhiteSpace(connection.AuthHeaderName) &&
            !string.IsNullOrWhiteSpace(connection.AuthHeaderValue))
        {
            request.Headers.Add(connection.AuthHeaderName, connection.AuthHeaderValue);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(connection.TimeoutSeconds));

        var client = httpClientFactory.CreateClient("control-plane-telemetry");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(timeoutCts.Token).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeoutCts.Token).ConfigureAwait(false);
        var root = document.RootElement;

        if (!root.TryGetProperty("status", out var statusElement) ||
            !string.Equals(statusElement.GetString(), "success", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Telemetry query backend returned a non-success status.");
        }

        var data = root.GetProperty("data");
        var resultType = data.GetProperty("resultType").GetString();

        return resultType switch
        {
            "scalar" => ParsePrometheusValue(data.GetProperty("result")),
            "vector" => ParsePrometheusVector(data.GetProperty("result")),
            _ => throw new InvalidOperationException($"Unsupported Prometheus result type '{resultType}'.")
        };
    }

    private static double? ParsePrometheusVector(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Array || result.GetArrayLength() == 0)
        {
            return null;
        }

        var sample = result[0];
        if (!sample.TryGetProperty("value", out var value))
        {
            return null;
        }

        return ParsePrometheusValue(value);
    }

    private static double? ParsePrometheusValue(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() < 2)
        {
            return null;
        }

        var raw = value[1].GetString();
        return double.TryParse(raw, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private sealed record DeployTelemetryPolicy
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

        public static DeployTelemetryPolicy? Parse(DeployOperationSpec spec)
        {
            var parameters = spec.Parameters;
            if (!parameters.TryGetValue("telemetry.connection", out var connectionId) ||
                string.IsNullOrWhiteSpace(connectionId))
            {
                return null;
            }

            var preset = ResolvePreset(spec);
            var errorRateQuery = Get(parameters, "telemetry.error_rate.query") ?? preset?.ErrorRateQuery;
            var latencyQuery = Get(parameters, "telemetry.latency_p95.query") ?? preset?.LatencyP95Query;
            var sampleQuery = Get(parameters, "telemetry.sample_count.query") ?? preset?.MinimumSampleQuery;

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

            var validationError = preset?.ValidationError;
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
                ValidationError = validationError
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
                DeployTargetKind.AwsEcs => HasCanarySignalConfiguration(spec.Parameters) ? "aws-alb-canary" : "honua-http",
                DeployTargetKind.AzureContainerApps => HasCanarySignalConfiguration(spec.Parameters) ? "azure-aca-canary" : "honua-http",
                DeployTargetKind.AzureFunctions => "honua-http",
                _ => null
            };

        private static bool HasCanarySignalConfiguration(IReadOnlyDictionary<string, string> parameters)
            => parameters.ContainsKey("telemetry.prometheus.canary_selector")
               || parameters.ContainsKey("telemetry.prometheus.canary_job");

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

    private sealed record ValidatedTelemetryConnection(
        Uri BaseUri,
        string QueryPath,
        string? AuthHeaderName,
        string? AuthHeaderValue,
        int TimeoutSeconds);
}
