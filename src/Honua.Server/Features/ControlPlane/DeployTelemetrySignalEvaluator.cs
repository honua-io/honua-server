// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Infrastructure.Validation;
using Microsoft.Extensions.Options;

namespace Honua.ControlPlane;

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
/// Multi-provider deploy telemetry gate. Parses the provider-neutral
/// <see cref="DeployTelemetryPolicy"/>, applies warmup, resolves the named telemetry connection,
/// then dispatches the metric reads to the <see cref="IDeployTelemetryProviderEvaluator"/> whose
/// <see cref="IDeployTelemetryProviderEvaluator.Provider"/> matches the connection's
/// <c>Provider</c>. An unknown/unconfigured provider surfaces an explicit, logged
/// "unsupported provider" wait rather than stalling silently forever.
/// </summary>
internal sealed class DeployTelemetrySignalEvaluator(
    IOptionsMonitor<ControlPlaneOptions> optionsMonitor,
    IEnumerable<IDeployTelemetryProviderEvaluator> providerEvaluators,
    ILogger<DeployTelemetrySignalEvaluator> logger) : IDeployTelemetrySignalEvaluator
{
    private readonly Dictionary<string, IDeployTelemetryProviderEvaluator> _providers =
        BuildProviderMap(providerEvaluators);

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

        var providerKey = connection.Provider?.Trim() ?? string.Empty;
        if (!_providers.TryGetValue(providerKey, out var providerEvaluator))
        {
            // Surface an explicit, logged signal rather than stalling silently forever. The
            // reconciler keeps the operation in Reconciling on WaitForMoreTelemetry, so the
            // log is the operator's breadcrumb that the connection's provider is unsupported.
            DeployTelemetrySignalEvaluatorLog.UnsupportedProvider(
                logger,
                operation.OperationId,
                connection.ConnectionId,
                string.IsNullOrWhiteSpace(providerKey) ? "(empty)" : providerKey,
                string.Join(", ", _providers.Keys.OrderBy(static key => key, StringComparer.Ordinal)));

            return new DeployTelemetryDecision
            {
                WaitForMoreTelemetry = true,
                Message = $"Waiting for telemetry confirmation because provider '{connection.Provider}' on connection '{connection.ConnectionId}' is not supported for deploy rollback signals."
            };
        }

        try
        {
            var readings = await providerEvaluator
                .ReadAsync(policy.ToDescriptor(), ToDescriptor(connection), cancellationToken)
                .ConfigureAwait(false);
            return Evaluate(policy, readings);
        }
        catch (Exception ex)
        {
            DeployTelemetrySignalEvaluatorLog.EvaluationFailed(logger, operation.OperationId, ex);
            return new DeployTelemetryDecision
            {
                WaitForMoreTelemetry = true,
                Message = "Waiting for telemetry confirmation because the telemetry query backend is unavailable."
            };
        }
    }

    /// <summary>
    /// Provider-neutral evaluation of telemetry readings against the policy thresholds. Keeps
    /// promote/rollback/wait semantics identical regardless of which metrics backend produced them.
    /// </summary>
    internal static DeployTelemetryDecision Evaluate(DeployTelemetryPolicy policy, DeployTelemetryReadings readings)
    {
        if (policy.MinimumSampleCount.HasValue &&
            (!readings.SampleCount.HasValue || readings.SampleCount.Value < policy.MinimumSampleCount.Value))
        {
            return new DeployTelemetryDecision
            {
                WaitForMoreTelemetry = true,
                Message = readings.SampleCount.HasValue
                    ? $"Waiting for telemetry confirmation because sample count {Format(readings.SampleCount.Value)} is below the required minimum {Format(policy.MinimumSampleCount.Value)}."
                    : "Waiting for telemetry confirmation because no sample-count signal is available yet."
            };
        }

        var breachMessages = new List<string>();
        var healthySignals = new List<string>();

        if (!string.IsNullOrWhiteSpace(policy.ErrorRateQuery) && policy.ErrorRateThreshold.HasValue)
        {
            if (readings.ErrorRate.HasValue && readings.ErrorRate.Value > policy.ErrorRateThreshold.Value)
            {
                breachMessages.Add(
                    $"error rate {Format(readings.ErrorRate.Value)} exceeded threshold {Format(policy.ErrorRateThreshold.Value)}");
            }
            else
            {
                healthySignals.Add("error-rate signal is within threshold");
            }
        }

        if (!string.IsNullOrWhiteSpace(policy.LatencyP95Query) && policy.LatencyP95ThresholdMs.HasValue)
        {
            if (readings.LatencyP95.HasValue && readings.LatencyP95.Value > policy.LatencyP95ThresholdMs.Value)
            {
                breachMessages.Add(
                    $"p95 latency {Format(readings.LatencyP95.Value)}ms exceeded threshold {Format(policy.LatencyP95ThresholdMs.Value)}ms");
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

    private static DeployTelemetryConnectionDescriptor ToDescriptor(DeployTelemetryConnectionOptions connection)
        => new()
        {
            ConnectionId = connection.ConnectionId,
            Provider = connection.Provider,
            BaseUrl = connection.BaseUrl,
            QueryPath = connection.QueryPath,
            AuthHeaderName = connection.AuthHeaderName,
            AuthHeaderValue = connection.AuthHeaderValue,
            Region = connection.Region,
            TimeoutSeconds = connection.TimeoutSeconds
        };

    private static string Format(double value)
        => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static Dictionary<string, IDeployTelemetryProviderEvaluator> BuildProviderMap(
        IEnumerable<IDeployTelemetryProviderEvaluator> providerEvaluators)
    {
        var map = new Dictionary<string, IDeployTelemetryProviderEvaluator>(StringComparer.OrdinalIgnoreCase);
        foreach (var evaluator in providerEvaluators)
        {
            // First registration wins so a host can override a built-in provider deliberately.
            map.TryAdd(evaluator.Provider, evaluator);
        }

        return map;
    }
}

/// <summary>
/// Prometheus-compatible telemetry provider used to gate deploy success and trigger rollback.
/// Reference implementation for the multi-provider gate.
/// </summary>
internal sealed class PrometheusDeployTelemetryProviderEvaluator(
    IHttpClientFactory httpClientFactory) : IDeployTelemetryProviderEvaluator
{
    public string Provider => "prometheus";

    public async Task<DeployTelemetryReadings> ReadAsync(
        DeployTelemetryPolicyDescriptor policy,
        DeployTelemetryConnectionDescriptor connection,
        CancellationToken cancellationToken)
    {
        var validatedConnection = await ValidateConnectionAsync(connection, cancellationToken).ConfigureAwait(false);

        var sampleCount = string.IsNullOrWhiteSpace(policy.MinimumSampleQuery)
            ? (double?)null
            : await ExecutePrometheusQueryAsync(validatedConnection, policy.MinimumSampleQuery, cancellationToken).ConfigureAwait(false);

        // Short-circuit when the sample-count gate already fails; mirrors the original
        // evaluator that did not query error-rate/latency until the minimum sample was met.
        if (policy.MinimumSampleCount.HasValue && (!sampleCount.HasValue || sampleCount.Value < policy.MinimumSampleCount.Value))
        {
            return new DeployTelemetryReadings { SampleCount = sampleCount };
        }

        double? errorRate = null;
        if (!string.IsNullOrWhiteSpace(policy.ErrorRateQuery) && policy.ErrorRateThreshold.HasValue)
        {
            errorRate = await ExecutePrometheusQueryAsync(validatedConnection, policy.ErrorRateQuery, cancellationToken).ConfigureAwait(false);
        }

        double? latencyP95 = null;
        if (!string.IsNullOrWhiteSpace(policy.LatencyP95Query) && policy.LatencyP95ThresholdMs.HasValue)
        {
            latencyP95 = await ExecutePrometheusQueryAsync(validatedConnection, policy.LatencyP95Query, cancellationToken).ConfigureAwait(false);
        }

        return new DeployTelemetryReadings
        {
            SampleCount = sampleCount,
            ErrorRate = errorRate,
            LatencyP95 = latencyP95
        };
    }

    private static async Task<ValidatedTelemetryConnection> ValidateConnectionAsync(
        DeployTelemetryConnectionDescriptor connection,
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

    private sealed record ValidatedTelemetryConnection(
        Uri BaseUri,
        string QueryPath,
        string? AuthHeaderName,
        string? AuthHeaderValue,
        int TimeoutSeconds);
}
