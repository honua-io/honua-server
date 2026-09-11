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

        // Before the candidate receives traffic there is no evidence to evaluate (#4617). Hold only until
        // the exposure deadline, then fail the rollout without ever activating the candidate: a deploy
        // that never reaches exposure must neither wait forever nor promote.
        if (operation.Status == WorkflowOperationStatus.Submitted && operation.Deploy.TrafficExposedAt == null)
        {
            var sinceCreated = DateTimeOffset.UtcNow - operation.CreatedAt;
            if (sinceCreated < policy.ExposureDeadline)
            {
                var remainingExposure = policy.ExposureDeadline - sinceCreated;
                return new DeployTelemetryDecision
                {
                    WaitForMoreTelemetry = true,
                    Message = $"Waiting for the candidate revision to receive traffic before evaluating telemetry ({Math.Ceiling(Math.Max(remainingExposure.TotalSeconds, 0))}s remaining before the exposure deadline)."
                };
            }

            return new DeployTelemetryDecision
            {
                RollbackRecommended = true,
                Message = $"Automatic rollback requested because the candidate revision was never observed receiving traffic within the {policy.ExposureDeadline.TotalSeconds:0}-second exposure deadline; the rollout is failed without activating the candidate."
            };
        }

        // Warmup/bake anchors on when the candidate actually started receiving traffic, not when the
        // operation record was created (#4617): backend provisioning time between the two can be
        // unbounded, during which no telemetry evidence is meaningful yet. Falls back to CreatedAt for
        // operations persisted before TrafficExposedAt was tracked, preserving prior behavior for them.
        var exposureAnchor = operation.Deploy.TrafficExposedAt ?? operation.CreatedAt;

        if (DateTimeOffset.UtcNow - exposureAnchor < policy.WarmupDuration)
        {
            var remaining = policy.WarmupDuration - (DateTimeOffset.UtcNow - exposureAnchor);
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
            var healthDecision = await EvaluateHealthProbeAsync(operation, policy, exposureAnchor, cancellationToken).ConfigureAwait(false);
            if (healthDecision != null)
            {
                return healthDecision;
            }
        }

        // Golden-query correctness gate (#2811): a status/5xx/p95-healthy release can still be corrupt
        // (200 OK, wrong/garbled body). This provider-independent gate asserts the response body matches
        // an operator-declared checksum/substring before the deploy is allowed to auto-promote. A failing
        // golden query drives the SAME rollback path as an error-rate breach (subject to the anti-flap
        // debounce); a passing one falls through to the metrics gate, which must also pass.
        if (policy.HasGoldenQuery)
        {
            var goldenDecision = await EvaluateGoldenQueryAsync(operation, policy, exposureAnchor, cancellationToken).ConfigureAwait(false);
            if (goldenDecision != null)
            {
                return goldenDecision;
            }
        }

        // Explicit probe-only profile (#4617): the readiness and golden-query probes above are the whole
        // gate, so no metrics connection is consulted. Metric-required profiles never take this path —
        // Parse rejects a probe-only gate that was not declared health-only.
        if (policy.IsHealthOnly)
        {
            return ApplyBreachDebounce(operation, new DeployTelemetryDecision
            {
                Message = "Telemetry gate passed: health-only profile probes are healthy."
            });
        }

        var connection = optionsMonitor.CurrentValue.TelemetryConnections
            .FirstOrDefault(candidate => string.Equals(candidate.ConnectionId, policy.ConnectionId, StringComparison.Ordinal));

        if (connection == null)
        {
            return BoundEvidenceWait(
                exposureAnchor,
                policy,
                $"Waiting for telemetry confirmation because connection '{policy.ConnectionId}' is not configured.");
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

            return BoundEvidenceWait(
                exposureAnchor,
                policy,
                $"Waiting for telemetry confirmation because provider '{connection.Provider}' on connection '{connection.ConnectionId}' is not supported for deploy rollback signals.");
        }

        try
        {
            var readings = await providerEvaluator
                .ReadAsync(policy.ToDescriptor(), ToDescriptor(connection), cancellationToken)
                .ConfigureAwait(false);
            var instantaneous = Evaluate(policy, readings);

            // Missing/insufficient evidence (absent metric, ambiguous/stale sample rejected by the
            // provider, sample floor not yet met) is bounded by the evidence grace window rather than
            // the anti-flap debounce, which exists for noisy instantaneous breaches, not sustained
            // absence (#4617). Genuine breaches and the healthy path are unaffected.
            return instantaneous.WaitForMoreTelemetry
                ? BoundEvidenceWait(exposureAnchor, policy, instantaneous.Message)
                : ApplyBreachDebounce(operation, instantaneous);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Intentional broad catch: a telemetry-backend failure must not fail the deploy
            // evaluation outright; it is mapped to a bounded wait-for-more-telemetry decision
            // below so a transient backend outage never silently promotes past an unverified
            // rollback gate.
            DeployTelemetrySignalEvaluatorLog.EvaluationFailed(logger, operation.OperationId, ex);
            return BoundEvidenceWait(
                exposureAnchor,
                policy,
                "Waiting for telemetry confirmation because the telemetry query backend is unavailable.");
        }
    }

    /// <summary>
    /// Bounds a "missing/invalid evidence" wait decision to <see cref="DeployTelemetryPolicy.EvidenceGraceDuration"/>
    /// past the end of warmup (honua-server#4617): once warmup has elapsed, evidence that never
    /// arrives (provider outage, unconfigured connection, unsupported provider, a probe with no
    /// backing service) must not park a deploy in Reconciling forever. Before the deadline this
    /// returns the same wait decision (message annotated with the remaining grace); at/after the
    /// deadline it escalates to a rollback recommendation. There is no missing-data success path.
    /// </summary>
    private static DeployTelemetryDecision BoundEvidenceWait(
        DateTimeOffset exposureAnchor,
        DeployTelemetryPolicy policy,
        string waitMessage)
    {
        var deadline = exposureAnchor + policy.WarmupDuration + policy.EvidenceGraceDuration;
        var now = DateTimeOffset.UtcNow;
        if (now < deadline)
        {
            var remaining = deadline - now;
            return new DeployTelemetryDecision
            {
                WaitForMoreTelemetry = true,
                Message = $"{waitMessage} Holding for up to {Math.Ceiling(Math.Max(remaining.TotalSeconds, 0))}s before escalating missing telemetry evidence to a rollback recommendation."
            };
        }

        return new DeployTelemetryDecision
        {
            RollbackRecommended = true,
            Message = $"Automatic rollback requested because telemetry evidence remained unavailable beyond the configured evidence grace window: {waitMessage}"
        };
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
        DateTimeOffset exposureAnchor,
        CancellationToken cancellationToken)
    {
        if (healthProbe == null)
        {
            return BoundEvidenceWait(
                exposureAnchor,
                policy,
                "Waiting for telemetry confirmation because a synthetic health probe is configured but no health-probe service is available.");
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
            return BoundEvidenceWait(
                exposureAnchor,
                policy,
                "Waiting for telemetry confirmation because the synthetic health probe could not be executed.");
        }

        if (!result.Validated)
        {
            return BoundEvidenceWait(
                exposureAnchor,
                policy,
                $"Waiting for telemetry confirmation because the synthetic health probe is misconfigured: {result.Detail}");
        }

        if (result.Failures >= policy.HealthProbeFailureThreshold)
        {
            var breach = new DeployTelemetryDecision
            {
                RollbackRecommended = true,
                Message = $"Automatic rollback requested because the synthetic health probe is unhealthy: {result.Failures} of {result.Attempts} checks did not return a healthy {policy.HealthProbeExpectedStatusCode} response (failure threshold {policy.HealthProbeFailureThreshold})."
            };

            return ApplyBreachDebounce(operation, breach);
        }

        // Healthy probe: do not promote on the probe alone. The caller falls through to the metrics
        // gate (whose own anti-flap debounce manages the breach streak), which must also pass before
        // the deploy is promoted.
        return null;
    }

    /// <summary>
    /// Runs the golden-query correctness probe and maps the outcome onto a deploy decision. Returns
    /// <see langword="null"/> when the response body matched (so the caller falls through to the metrics
    /// gate); otherwise returns a rollback recommendation (subject to the anti-flap debounce) on a
    /// content mismatch, or a bounded wait when the probe could not run (no probe service, misconfigured
    /// URL, or transport failure) so a corrupt-but-200 release never silently promotes.
    /// </summary>
    private async Task<DeployTelemetryDecision?> EvaluateGoldenQueryAsync(
        WorkflowOperationRecord operation,
        DeployTelemetryPolicy policy,
        DateTimeOffset exposureAnchor,
        CancellationToken cancellationToken)
    {
        if (healthProbe == null)
        {
            return BoundEvidenceWait(
                exposureAnchor,
                policy,
                "Waiting for telemetry confirmation because a golden-query correctness gate is configured but no probe service is available.");
        }

        DeployGoldenQueryResult result;
        try
        {
            result = await healthProbe
                .ProbeGoldenQueryAsync(
                    new DeployGoldenQueryRequest
                    {
                        Url = policy.GoldenQueryUrl!,
                        ExpectedSha256 = policy.GoldenQueryExpectedSha256,
                        ExpectedBodyContains = policy.GoldenQueryExpectedContains,
                        ForbiddenBodyContains = policy.GoldenQueryForbiddenContains,
                        ExpectedStatusCode = policy.GoldenQueryExpectedStatusCode,
                        TimeoutSeconds = policy.GoldenQueryTimeoutSeconds
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            DeployTelemetrySignalEvaluatorLog.EvaluationFailed(logger, operation.OperationId, ex);
            return BoundEvidenceWait(
                exposureAnchor,
                policy,
                "Waiting for telemetry confirmation because the golden-query correctness probe could not be executed.");
        }

        if (!result.Validated)
        {
            return BoundEvidenceWait(
                exposureAnchor,
                policy,
                $"Waiting for telemetry confirmation because the golden-query correctness gate is misconfigured: {result.Detail}");
        }

        if (!result.Matched)
        {
            var breach = new DeployTelemetryDecision
            {
                RollbackRecommended = true,
                Message = $"Automatic rollback requested because the release failed the golden-query correctness gate: {result.Detail}"
            };

            return ApplyBreachDebounce(operation, breach);
        }

        // Correct release: fall through to the metrics gate (which must also pass before promotion).
        return null;
    }

    /// <summary>
    /// Provider-neutral evaluation of telemetry readings against the policy thresholds. Keeps
    /// promote/rollback/wait semantics identical regardless of which metrics backend produced them.
    /// </summary>
    internal static DeployTelemetryDecision Evaluate(DeployTelemetryPolicy policy, DeployTelemetryReadings readings)
    {
        readings = WithoutInvalidReadings(readings);

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
        var missingSignals = new List<string>();

        // A configured signal with no usable reading (absent, or rejected by the provider as
        // non-finite/ambiguous/stale) must never fall through to "healthy" (#4617) — that is exactly
        // how a real degradation goes undetected when the sample floor is unconfigured. It is treated
        // as still-waiting evidence instead, which the caller bounds via the evidence grace window.
        if (!string.IsNullOrWhiteSpace(policy.ErrorRateQuery) && policy.ErrorRateThreshold.HasValue)
        {
            if (!readings.ErrorRate.HasValue)
            {
                missingSignals.Add("no error-rate signal is available yet");
            }
            else if (readings.ErrorRate.Value > policy.ErrorRateThreshold.Value)
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
            if (!readings.LatencyP95.HasValue)
            {
                missingSignals.Add("no latency signal is available yet");
            }
            else if (readings.LatencyP95.Value > policy.LatencyP95ThresholdMs.Value)
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

        if (missingSignals.Count > 0)
        {
            return new DeployTelemetryDecision
            {
                WaitForMoreTelemetry = true,
                Message = $"Waiting for telemetry confirmation: {string.Join("; ", missingSignals)}."
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
    /// Provider-neutral evidence validity guard (#4617): a non-finite (NaN/±Infinity) or negative
    /// sample count, error rate or latency is not a measurement — metric math over an undefined ratio
    /// or a counter reset produces them — so it is treated as absent. Absent evidence never satisfies
    /// a configured requirement and is bounded by the evidence grace window, whichever provider
    /// produced it.
    /// </summary>
    private static DeployTelemetryReadings WithoutInvalidReadings(DeployTelemetryReadings readings)
        => readings with
        {
            SampleCount = UsableReading(readings.SampleCount),
            ErrorRate = UsableReading(readings.ErrorRate),
            LatencyP95 = UsableReading(readings.LatencyP95)
        };

    private static double? UsableReading(double? value)
        => value is { } reading && double.IsFinite(reading) && reading >= 0 ? reading : null;

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
            TimeoutSeconds = connection.TimeoutSeconds,
            AllowPrivateNetworks = connection.AllowPrivateNetworks
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
            : await ExecutePrometheusQueryAsync(validatedConnection, policy.MinimumSampleQuery, policy.MaximumEvidenceStaleness, cancellationToken).ConfigureAwait(false);

        // Short-circuit when the sample-count gate already fails; mirrors the original
        // evaluator that did not query error-rate/latency until the minimum sample was met.
        if (policy.MinimumSampleCount.HasValue && (!sampleCount.HasValue || sampleCount.Value < policy.MinimumSampleCount.Value))
        {
            return new DeployTelemetryReadings { SampleCount = sampleCount };
        }

        double? errorRate = null;
        if (!string.IsNullOrWhiteSpace(policy.ErrorRateQuery) && policy.ErrorRateThreshold.HasValue)
        {
            errorRate = await ExecutePrometheusQueryAsync(validatedConnection, policy.ErrorRateQuery, policy.MaximumEvidenceStaleness, cancellationToken).ConfigureAwait(false);
        }

        double? latencyP95 = null;
        if (!string.IsNullOrWhiteSpace(policy.LatencyP95Query) && policy.LatencyP95ThresholdMs.HasValue)
        {
            latencyP95 = await ExecutePrometheusQueryAsync(validatedConnection, policy.LatencyP95Query, policy.MaximumEvidenceStaleness, cancellationToken).ConfigureAwait(false);
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
            .ValidateAsync(connection.BaseUrl, connection.AllowPrivateNetworks, cancellationToken)
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
        TimeSpan? maximumStaleness,
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
            "scalar" => ParsePrometheusValue(data.GetProperty("result"), maximumStaleness),
            "vector" => ParsePrometheusVector(data.GetProperty("result"), maximumStaleness),
            _ => throw new InvalidOperationException($"Unsupported Prometheus result type '{resultType}'.")
        };
    }

    private static double? ParsePrometheusVector(JsonElement result, TimeSpan? maximumStaleness)
    {
        if (result.ValueKind != JsonValueKind.Array || result.GetArrayLength() == 0)
        {
            return null;
        }

        // A well-formed query (sum(), avg(), a single selector match, …) resolves to exactly one
        // series. More than one is an ambiguous-cardinality configuration error (#4617): silently
        // taking result[0] can promote or hold a deploy on an arbitrary, unintended series. Surface
        // it as a failure so the caller's bounded-grace handling applies instead of a silent guess.
        if (result.GetArrayLength() > 1)
        {
            throw new InvalidOperationException(
                $"Telemetry query resolved to {result.GetArrayLength()} series; expected exactly one. " +
                "Aggregate the query (for example with sum()/avg()) or narrow the selector.");
        }

        var sample = result[0];
        if (!sample.TryGetProperty("value", out var value))
        {
            return null;
        }

        return ParsePrometheusValue(value, maximumStaleness);
    }

    private static double? ParsePrometheusValue(JsonElement value, TimeSpan? maximumStaleness)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() < 2)
        {
            return null;
        }

        if (maximumStaleness.HasValue)
        {
            var sampleUnixSeconds = value[0].ValueKind switch
            {
                JsonValueKind.Number => value[0].GetDouble(),
                JsonValueKind.String when double.TryParse(
                    value[0].GetString(),
                    NumberStyles.Float | NumberStyles.AllowThousands,
                    CultureInfo.InvariantCulture,
                    out var parsedTimestamp) => parsedTimestamp,
                _ => (double?)null
            };

            // An unparseable or missing observation timestamp is exactly the "ignores the
            // observation timestamp" gap this bound closes (#4617): treat it as absent rather than
            // assume freshness.
            if (!sampleUnixSeconds.HasValue)
            {
                return null;
            }

            // Skew in either direction beyond the bound (an old cached answer, or a timestamp from the
            // future) means the sample cannot be trusted to describe the candidate now.
            var sampleUnixMilliseconds = sampleUnixSeconds.Value * 1000;
            if (!double.IsFinite(sampleUnixMilliseconds) ||
                sampleUnixMilliseconds < DateTimeOffset.MinValue.ToUnixTimeMilliseconds() ||
                sampleUnixMilliseconds > DateTimeOffset.MaxValue.ToUnixTimeMilliseconds())
            {
                return null;
            }

            var observedAt = DateTimeOffset.FromUnixTimeMilliseconds((long)sampleUnixMilliseconds);
            if ((DateTimeOffset.UtcNow - observedAt).Duration() > maximumStaleness.Value)
            {
                return null;
            }
        }

        var raw = value[1].GetString();
        if (!double.TryParse(raw, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var parsed))
        {
            return null;
        }

        // Prometheus legitimately returns "NaN"/"+Inf"/"-Inf" for undefined expressions (for
        // example a 0/0 rate). Treating a non-finite value as a real reading can silently pass an
        // ill-defined signal through the threshold comparison (#4617); absent is the correct and
        // already-handled semantics.
        return double.IsFinite(parsed) ? parsed : null;
    }

    private sealed record ValidatedTelemetryConnection(
        Uri BaseUri,
        string QueryPath,
        string? AuthHeaderName,
        string? AuthHeaderValue,
        int TimeoutSeconds);
}
