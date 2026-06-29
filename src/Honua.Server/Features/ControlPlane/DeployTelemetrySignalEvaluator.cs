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

    /// <summary>
    /// When set, the deploy spec parameters the reconciler should persist alongside the decision.
    /// Used by the anti-flap debounce to carry the consecutive-breach streak between reconcile
    /// cycles without a separate store. Null leaves the existing parameters untouched.
    /// </summary>
    public IReadOnlyDictionary<string, string>? UpdatedDeployParameters { get; init; }
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
    ILogger<DeployTelemetrySignalEvaluator> logger,
    IDeployHealthProbe? healthProbe = null) : IDeployTelemetrySignalEvaluator
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
            // Bounded wait (#2161): an invalid policy is a configuration error that will never
            // self-heal from telemetry, so an unbounded WaitForMoreTelemetry silently parks the
            // deploy in Reconciling forever. Hold for a finite grace window (so a transient
            // mid-edit config is tolerated), then escalate to a rollback recommendation rather
            // than promoting a deploy whose health gate is broken. The grace window is bounded
            // even when misconfigured to a non-positive value.
            var grace = ResolveInvalidPolicyGrace(operation.Deploy);
            var elapsed = DateTimeOffset.UtcNow - operation.CreatedAt;
            var validationDetail = policy.ValidationError ?? "Deploy telemetry policy is invalid.";
            if (elapsed < grace)
            {
                var remaining = grace - elapsed;
                return new DeployTelemetryDecision
                {
                    WaitForMoreTelemetry = true,
                    Message = $"{validationDetail} Holding for up to {Math.Ceiling(Math.Max(remaining.TotalSeconds, 0))}s before escalating an invalid telemetry policy."
                };
            }

            return new DeployTelemetryDecision
            {
                RollbackRecommended = true,
                Message = $"Automatic rollback requested because the deploy telemetry policy is invalid and could not be evaluated within the configured grace window: {validationDetail}"
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

        // Synthetic /healthz/ready gate (#1849): a provider-independent rollback trigger inherited by
        // every backend and change class. An unhealthy probe short-circuits to the same rollback path
        // as an error-rate/latency breach (subject to the anti-flap debounce). A healthy probe does not
        // promote on its own — it falls through to the metrics gate, which must also pass.
        if (policy.HasHealthProbe)
        {
            var healthDecision = await EvaluateHealthProbeAsync(operation, policy, cancellationToken).ConfigureAwait(false);
            if (healthDecision != null)
            {
                return healthDecision;
            }
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
            var instantaneous = Evaluate(policy, readings);
            return ApplyBreachDebounce(operation, instantaneous);
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
    /// Runs the synthetic health probe and maps the outcome onto a deploy decision. Returns
    /// <see langword="null"/> when the probe is healthy (so the caller can fall through to the metrics
    /// gate); otherwise returns a rollback recommendation (subject to the anti-flap debounce) or a
    /// bounded wait when the probe could not run (no probe service, misconfigured URL, or transport
    /// failure) so a misconfiguration never silently promotes past an unverified health gate.
    /// </summary>
    private async Task<DeployTelemetryDecision?> EvaluateHealthProbeAsync(
        WorkflowOperationRecord operation,
        DeployTelemetryPolicy policy,
        CancellationToken cancellationToken)
    {
        if (healthProbe == null)
        {
            return new DeployTelemetryDecision
            {
                WaitForMoreTelemetry = true,
                Message = "Waiting for telemetry confirmation because a synthetic health probe is configured but no health-probe service is available."
            };
        }

        DeployHealthProbeResult result;
        try
        {
            result = await healthProbe
                .ProbeAsync(
                    new DeployHealthProbeRequest
                    {
                        Url = policy.HealthProbeUrl!,
                        Samples = policy.HealthProbeSamples,
                        ExpectedStatusCode = policy.HealthProbeExpectedStatusCode,
                        TimeoutSeconds = policy.HealthProbeTimeoutSeconds
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            DeployTelemetrySignalEvaluatorLog.EvaluationFailed(logger, operation.OperationId, ex);
            return new DeployTelemetryDecision
            {
                WaitForMoreTelemetry = true,
                Message = "Waiting for telemetry confirmation because the synthetic health probe could not be executed."
            };
        }

        if (!result.Validated)
        {
            return new DeployTelemetryDecision
            {
                WaitForMoreTelemetry = true,
                Message = $"Waiting for telemetry confirmation because the synthetic health probe is misconfigured: {result.Detail}"
            };
        }

        if (result.Failures >= policy.HealthProbeFailureThreshold)
        {
            var breach = new DeployTelemetryDecision
            {
                RollbackRecommended = true,
                Message = $"Automatic rollback requested because the synthetic health probe is unhealthy: {result.Failures} of {result.Attempts} checks did not return {policy.HealthProbeExpectedStatusCode} (failure threshold {policy.HealthProbeFailureThreshold})."
            };

            return ApplyBreachDebounce(operation, breach);
        }

        // Healthy probe: do not promote on the probe alone. The caller falls through to the metrics
        // gate (whose own anti-flap debounce manages the breach streak), which must also pass before
        // the deploy is promoted.
        return null;
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

    /// <summary>
    /// Reserved deploy-spec parameter key that an operator sets to require N consecutive breaching
    /// scrapes before a telemetry-driven rollback fires. Defaults to single-scrape behavior (1) when
    /// unset or invalid, preserving the historical instantaneous gate.
    /// </summary>
    internal const string BreachDebounceThresholdParameterKey = "telemetry.rollback.consecutive_breaches";

    /// <summary>
    /// Reserved deploy-spec parameter key the evaluator uses to carry the current consecutive-breach
    /// streak between reconcile cycles. The reconciler persists the updated parameters returned on the
    /// decision, so the streak survives without a separate store.
    /// </summary>
    internal const string BreachStreakParameterKey = "telemetry.rollback.breach_streak";

    /// <summary>
    /// Reserved deploy-spec parameter key bounding how long an invalid telemetry policy is tolerated
    /// before the gate escalates to a rollback recommendation. Defaults to 15 minutes.
    /// </summary>
    internal const string InvalidPolicyGraceSecondsParameterKey = "telemetry.invalid_policy_grace_seconds";

    private static readonly TimeSpan DefaultInvalidPolicyGrace = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan MaximumInvalidPolicyGrace = TimeSpan.FromHours(1);

    private static TimeSpan ResolveInvalidPolicyGrace(DeployOperationSpec? spec)
    {
        if (spec != null &&
            spec.Parameters.TryGetValue(InvalidPolicyGraceSecondsParameterKey, out var raw) &&
            double.TryParse(raw, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var seconds) &&
            seconds > 0)
        {
            // Clamp so a misconfiguration can never re-create the unbounded-park bug.
            return seconds >= MaximumInvalidPolicyGrace.TotalSeconds
                ? MaximumInvalidPolicyGrace
                : TimeSpan.FromSeconds(seconds);
        }

        return DefaultInvalidPolicyGrace;
    }

    /// <summary>
    /// Opt-in N-consecutive-breach anti-flap. A single noisy scrape (GP metrics are burstier) must not
    /// trigger a production rollback. When the operator configures a debounce threshold &gt; 1, a
    /// breach is only escalated to a rollback once that many consecutive scrapes breach; any healthy
    /// scrape resets the streak. When the threshold is 1 (the default) this is a no-op and the
    /// instantaneous decision is returned unchanged.
    /// </summary>
    private static DeployTelemetryDecision ApplyBreachDebounce(
        WorkflowOperationRecord operation,
        DeployTelemetryDecision instantaneous)
    {
        var spec = operation.Deploy;
        if (spec == null)
        {
            return instantaneous;
        }

        var threshold = ResolveBreachDebounceThreshold(spec);
        if (threshold <= 1)
        {
            return instantaneous;
        }

        var priorStreak = ResolveBreachStreak(spec);

        // A healthy or waiting scrape resets the streak; only an instantaneous rollback recommendation
        // contributes to it.
        if (!instantaneous.RollbackRecommended)
        {
            return priorStreak == 0
                ? instantaneous
                : instantaneous with { UpdatedDeployParameters = WithBreachStreak(spec.Parameters, 0) };
        }

        var newStreak = priorStreak + 1;
        if (newStreak >= threshold)
        {
            return instantaneous with
            {
                Message = $"{instantaneous.Message} ({newStreak} consecutive breaching scrapes reached the configured anti-flap threshold of {threshold}.)",
                UpdatedDeployParameters = WithBreachStreak(spec.Parameters, 0)
            };
        }

        // Below threshold: suppress the rollback this cycle and keep waiting for confirmation.
        return new DeployTelemetryDecision
        {
            WaitForMoreTelemetry = true,
            Message = $"Holding deploy: telemetry breached once ({newStreak} of {threshold} consecutive breaching scrapes required before rollback). {instantaneous.Message}",
            UpdatedDeployParameters = WithBreachStreak(spec.Parameters, newStreak)
        };
    }

    /// <summary>
    /// GP-aware anti-flap default. A serverless geoprocessing (AWS Batch) substrate deploy
    /// (<c>telemetry.policy = gp-batch</c>) carries burstier per-job metrics, so when the operator
    /// has not pinned an explicit consecutive-breach threshold it defaults to requiring several
    /// breaching scrapes rather than the single-scrape default, so one noisy GP burst does not
    /// trigger a production rollback (honua-server#2165, building on the #2161 anti-flap gate).
    /// </summary>
    internal const int GpBatchDefaultBreachDebounceThreshold = 3;

    private static int ResolveBreachDebounceThreshold(DeployOperationSpec spec)
    {
        if (spec.Parameters.TryGetValue(BreachDebounceThresholdParameterKey, out var raw) &&
            int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) &&
            parsed > 0)
        {
            return parsed;
        }

        // No explicit threshold: GP substrate deploys default to the burst-tolerant streak.
        return IsGpBatchDeploy(spec) ? GpBatchDefaultBreachDebounceThreshold : 1;
    }

    private static bool IsGpBatchDeploy(DeployOperationSpec spec)
        => spec.Parameters.TryGetValue("telemetry.policy", out var policyName)
            && (string.Equals(policyName?.Trim(), DeployTelemetryPolicy.GpBatchPolicyName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(policyName?.Trim(), "serverless-gp", StringComparison.OrdinalIgnoreCase));

    private static int ResolveBreachStreak(DeployOperationSpec spec)
        => spec.Parameters.TryGetValue(BreachStreakParameterKey, out var raw) &&
            int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) &&
            parsed > 0
                ? parsed
                : 0;

    private static Dictionary<string, string> WithBreachStreak(
        IReadOnlyDictionary<string, string> parameters,
        int streak)
        => new(parameters, StringComparer.Ordinal)
        {
            [BreachStreakParameterKey] = streak.ToString(CultureInfo.InvariantCulture)
        };

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
