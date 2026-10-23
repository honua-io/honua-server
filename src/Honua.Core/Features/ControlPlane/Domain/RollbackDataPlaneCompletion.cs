// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;

namespace Honua.Core.Features.ControlPlane.Domain;

/// <summary>
/// Verdict of the bounded functional query against the restored serving plane.
/// </summary>
public enum RollbackFunctionalQueryVerdict
{
    /// <summary>The query was not configured or could not be attempted.</summary>
    NotProven = 0,

    /// <summary>The response carried the prior revision's marker and no forbidden marker.</summary>
    MatchedPriorMarker = 1,

    /// <summary>The response carried another revision's marker or failed the declared expectation.</summary>
    ServedOtherMarker = 2,

    /// <summary>The response was an error envelope rather than a functional result.</summary>
    ServedErrorEnvelope = 3,

    /// <summary>The restored endpoint did not answer within the probe bound.</summary>
    Unreachable = 4
}

/// <summary>
/// Disposition of a rollback whose provider routing may already look converged.
/// </summary>
public enum RollbackDataPlaneDisposition
{
    /// <summary>Provider routing has not converged. Keep polling.</summary>
    StillSettling = 0,

    /// <summary>Routing converged, but identity, readiness, or the functional query is not proven yet and the window is still open.</summary>
    AwaitingDataPlane = 1,

    /// <summary>Prior revision identity, readiness, and the functional query are all proven.</summary>
    RolledBack = 2,

    /// <summary>The restored plane answered, but the functional query failed.</summary>
    Failed = 3,

    /// <summary>The restored endpoint is missing, unhealthy, or not the requested revision.</summary>
    ManualInterventionRequired = 4
}

/// <summary>
/// Evidence a backend has collected for the restored serving plane. The completion
/// function does not re-query providers; callers pass the authoritative serving
/// revision (never a sibling canary identity) and the readiness sample.
/// </summary>
public sealed record RollbackDataPlaneEvidence
{
    /// <summary>Provider routing (weights, alias, traffic, or proxy) matches the rollback shape.</summary>
    public bool RoutingConverged { get; init; }

    /// <summary>The authoritative serving revision is the requested prior revision.</summary>
    public bool PriorRevisionIdentityProven { get; init; }

    /// <summary>
    /// An authoritative serving revision was read and it is not the requested prior revision.
    /// A missing serving revision is not a mismatch; it stays unproven until the window closes.
    /// </summary>
    public bool ServingIdentityMismatched { get; init; }

    /// <summary>Task, image, or alias of the restored endpoint. Not a sibling canary identity.</summary>
    public string? ServingRevision { get; init; }

    /// <summary>Healthy restored endpoints. Zero cannot complete a rollback.</summary>
    public int HealthyEndpointCount { get; init; }

    /// <summary>Registered restored endpoints, including unhealthy ones. Zero means the target is empty.</summary>
    public int RegisteredEndpointCount { get; init; }

    /// <summary>Bounded functional-query verdict.</summary>
    public RollbackFunctionalQueryVerdict FunctionalQuery { get; init; }

    /// <summary>
    /// When provider observations first entered the rollback-settling state. Null means the
    /// window has not started, so missing proof stays non-terminal.
    /// </summary>
    public DateTimeOffset? RollbackStartedAt { get; init; }

    /// <summary>How long nominal convergence may wait for a healthy restored endpoint.</summary>
    public TimeSpan Window { get; init; }
}

/// <summary>Decision returned by <see cref="RollbackDataPlaneCompletion.Evaluate"/>.</summary>
public sealed record RollbackDataPlaneDecision
{
    /// <summary>What the backend operation is allowed to report.</summary>
    public required RollbackDataPlaneDisposition Disposition { get; init; }

    /// <summary>Authoritative serving revision when one was supplied. Never a canary substitute.</summary>
    public string? ObservedRevision { get; init; }

    /// <summary>Stable reason code for receipts and tests.</summary>
    public required string ReasonCode { get; init; }

    /// <summary>Operator-facing explanation. Includes serving revision and target health counts.</summary>
    public required string Message { get; init; }
}

/// <summary>Declared functional-query expectation read from deploy parameters.</summary>
public sealed record RollbackFunctionalQueryExpectation
{
    /// <summary>Absolute probe URL for backends that can reach a public endpoint.</summary>
    public string? Url { get; init; }

    /// <summary>Replica-local path for the self-hosted rolling backend. Loopback is not an outbound URL.</summary>
    public string? Path { get; init; }

    /// <summary>Substring the prior revision's response must contain.</summary>
    public string? ExpectedContains { get; init; }

    /// <summary>Substring that means another revision or a bad result was served.</summary>
    public string? ForbiddenContains { get; init; }

    /// <summary>Expected lowercase hex SHA-256 of the response body.</summary>
    public string? ExpectedSha256 { get; init; }

    /// <summary>HTTP status that counts as a servable response.</summary>
    public int ExpectedStatusCode { get; init; } = 200;

    /// <summary>Per-request timeout in seconds.</summary>
    public int TimeoutSeconds { get; init; } = 5;

    /// <summary>True when a body expectation is configured. A URL alone is not a proof.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ExpectedContains) || !string.IsNullOrWhiteSpace(ExpectedSha256);
}

/// <summary>Readiness probe request for backends whose provider API has no target-health signal.</summary>
public sealed record RollbackReadinessProbeRequest
{
    /// <summary>Absolute HTTPS readiness URL.</summary>
    public required string Url { get; init; }

    /// <summary>Status code that counts as ready.</summary>
    public int ExpectedStatusCode { get; init; } = 200;

    /// <summary>Per-request timeout in seconds.</summary>
    public int TimeoutSeconds { get; init; } = 5;
}

/// <summary>Result of a readiness probe.</summary>
public sealed record RollbackReadinessProbeResult
{
    /// <summary>True when every sample returned the expected status and was not an error envelope.</summary>
    public bool Proven { get; init; }

    /// <summary>Operator-safe detail.</summary>
    public string? Detail { get; init; }
}

/// <summary>Functional-query probe request.</summary>
public sealed record RollbackFunctionalProbeRequest
{
    /// <summary>Absolute HTTPS URL.</summary>
    public required string Url { get; init; }

    /// <summary>Required prior-revision marker.</summary>
    public string? ExpectedContains { get; init; }

    /// <summary>Forbidden marker (the candidate revision or an error token).</summary>
    public string? ForbiddenContains { get; init; }

    /// <summary>Expected body checksum.</summary>
    public string? ExpectedSha256 { get; init; }

    /// <summary>Status code that counts as servable.</summary>
    public int ExpectedStatusCode { get; init; } = 200;

    /// <summary>Per-request timeout in seconds.</summary>
    public int TimeoutSeconds { get; init; } = 5;
}

/// <summary>Functional-query probe result. The verdict is consumed by <see cref="RollbackDataPlaneCompletion"/>.</summary>
public sealed record RollbackFunctionalProbeResult
{
    /// <summary>Classification of the response.</summary>
    public required RollbackFunctionalQueryVerdict Verdict { get; init; }

    /// <summary>Operator-safe detail. Must not include secret material.</summary>
    public string? Detail { get; init; }
}

/// <summary>
/// Probe port for rollback readiness and the functional query. Provider target-health APIs stay on
/// the backend clients; this port is the HTTP proof for backends that do not have one, and the
/// functional query for every automatic backend that can reach a public URL.
/// </summary>
public interface IRollbackDataPlaneProbe
{
    /// <summary>Probes a readiness URL.</summary>
    Task<RollbackReadinessProbeResult> ProbeReadinessAsync(
        RollbackReadinessProbeRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Probes the functional query and classifies the body.</summary>
    Task<RollbackFunctionalProbeResult> ProbeFunctionalQueryAsync(
        RollbackFunctionalProbeRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Single completion rule for automatic rollback. A backend must not report
/// <see cref="WorkflowOperationStatus.RolledBack"/> until this function returns
/// <see cref="RollbackDataPlaneDisposition.RolledBack"/>. Nominal routing convergence with no
/// healthy restored endpoint stays non-terminal until <see cref="RollbackDataPlaneEvidence.Window"/>
/// elapses, then becomes <see cref="RollbackDataPlaneDisposition.Failed"/> or
/// <see cref="RollbackDataPlaneDisposition.ManualInterventionRequired"/>.
/// </summary>
public static class RollbackDataPlaneCompletion
{
    /// <summary>Matches <c>DeployWorkflowReconciler</c>'s rollback observation clock.</summary>
    public const string ObservationStartedAtParameterKey = "deployment.rollback.observation_started_at";

    /// <summary>Matches <c>DeployWorkflowReconciler</c>'s rollback observation budget.</summary>
    public const string ObservationWindowSecondsParameterKey = "deployment.rollback.observation_timeout_seconds";

    /// <summary>Same key the telemetry gate uses for the synthetic readiness URL.</summary>
    public const string ReadinessUrlParameterKey = "telemetry.healthz.url";

    /// <summary>Optional expected status for <see cref="ReadinessUrlParameterKey"/>.</summary>
    public const string ReadinessExpectedStatusParameterKey = "telemetry.healthz.expected_status";

    /// <summary>Optional timeout for <see cref="ReadinessUrlParameterKey"/>.</summary>
    public const string ReadinessTimeoutSecondsParameterKey = "telemetry.healthz.timeout_seconds";

    /// <summary>Same key the telemetry gate uses for the golden-query URL.</summary>
    public const string FunctionalQueryUrlParameterKey = "telemetry.golden_query.url";

    /// <summary>
    /// Replica-local functional path used by the self-hosted rolling backend.
    /// Not a <c>telemetry.*</c> key: loopback probes are not the outbound golden-query URL.
    /// </summary>
    public const string FunctionalQueryPathParameterKey = "rollback.functional_query.path";

    /// <summary>Prior-revision marker when the probe is local and no outbound golden-query URL is configured.</summary>
    public const string LocalFunctionalQueryExpectedContainsParameterKey = "rollback.functional_query.expected_contains";

    /// <summary>Forbidden marker for the local functional query.</summary>
    public const string LocalFunctionalQueryForbiddenContainsParameterKey = "rollback.functional_query.forbidden_contains";

    /// <summary>Expected SHA-256 for the local functional query.</summary>
    public const string LocalFunctionalQueryExpectedSha256ParameterKey = "rollback.functional_query.expected_sha256";

    /// <summary>Prior-revision marker the functional response must contain.</summary>
    public const string FunctionalQueryExpectedContainsParameterKey = "telemetry.golden_query.expected_contains";

    /// <summary>Marker the functional response must not contain (candidate revision or wrong result).</summary>
    public const string FunctionalQueryForbiddenContainsParameterKey = "telemetry.golden_query.forbidden_contains";

    /// <summary>Expected SHA-256 of the functional response body.</summary>
    public const string FunctionalQueryExpectedSha256ParameterKey = "telemetry.golden_query.expected_sha256";

    /// <summary>Expected HTTP status of the functional response.</summary>
    public const string FunctionalQueryExpectedStatusParameterKey = "telemetry.golden_query.expected_status";

    /// <summary>Per-request timeout for the functional query.</summary>
    public const string FunctionalQueryTimeoutSecondsParameterKey = "telemetry.golden_query.timeout_seconds";

    /// <summary>Reason: provider routing has not converged.</summary>
    public const string ReasonRoutingNotConverged = "rollback.routing_not_converged";

    /// <summary>Reason: serving identity is not the requested prior revision.</summary>
    public const string ReasonIdentityMismatch = "rollback.identity_mismatch";

    /// <summary>Reason: restored data-plane proof is still inside the window.</summary>
    public const string ReasonAwaitingDataPlane = "rollback.awaiting_data_plane";

    /// <summary>Reason: identity, readiness, and the functional query are proven.</summary>
    public const string ReasonRolledBack = "rollback.data_plane_proven";

    /// <summary>Reason: no healthy restored endpoint after the window.</summary>
    public const string ReasonReadinessUnproven = "rollback.readiness_unproven";

    /// <summary>Reason: the functional query failed after the window.</summary>
    public const string ReasonFunctionalQueryFailed = "rollback.functional_query_failed";

    /// <summary>Reason: the functional query was not configured or not attempted before the window closed.</summary>
    public const string ReasonFunctionalQueryUnproven = "rollback.functional_query_unproven";

    /// <summary>Default bound. Matches the reconciler's rollback observation timeout.</summary>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(5);

    /// <summary>Upper bound so a misconfigured window cannot park a rollback forever.</summary>
    public static readonly TimeSpan MaximumWindow = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Decides whether a backend operation may terminate as rolled back.
    /// </summary>
    public static RollbackDataPlaneDecision Evaluate(RollbackDataPlaneEvidence evidence, DateTimeOffset utcNow)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        if (!evidence.RoutingConverged)
        {
            return Decide(
                evidence,
                RollbackDataPlaneDisposition.StillSettling,
                ReasonRoutingNotConverged,
                "Provider routing has not converged on the restored revision.");
        }

        if (evidence.ServingIdentityMismatched)
        {
            return Decide(
                evidence,
                RollbackDataPlaneDisposition.ManualInterventionRequired,
                ReasonIdentityMismatch,
                "The authoritative serving revision is not the requested prior revision.");
        }

        var identityProven = evidence.PriorRevisionIdentityProven &&
            !string.IsNullOrWhiteSpace(evidence.ServingRevision);
        var readinessProven = evidence.HealthyEndpointCount > 0;
        var queryProven = evidence.FunctionalQuery == RollbackFunctionalQueryVerdict.MatchedPriorMarker;

        if (identityProven && readinessProven && queryProven)
        {
            return Decide(
                evidence,
                RollbackDataPlaneDisposition.RolledBack,
                ReasonRolledBack,
                "Restored revision identity, readiness, and the functional query are proven.");
        }

        if (!IsWindowExpired(evidence, utcNow))
        {
            return Decide(
                evidence,
                RollbackDataPlaneDisposition.AwaitingDataPlane,
                ReasonAwaitingDataPlane,
                "Routing converged, but the restored data plane is not proven yet.");
        }

        if (!readinessProven)
        {
            return Decide(
                evidence,
                RollbackDataPlaneDisposition.ManualInterventionRequired,
                ReasonReadinessUnproven,
                evidence.RegisteredEndpointCount <= 0
                    ? "Routing converged, but the restored target has no registered endpoints."
                    : "Routing converged, but the restored target has no healthy endpoints.");
        }

        if (evidence.FunctionalQuery is RollbackFunctionalQueryVerdict.ServedOtherMarker
            or RollbackFunctionalQueryVerdict.ServedErrorEnvelope
            or RollbackFunctionalQueryVerdict.Unreachable)
        {
            return Decide(
                evidence,
                RollbackDataPlaneDisposition.Failed,
                ReasonFunctionalQueryFailed,
                "The restored endpoint failed the functional query.");
        }

        return Decide(
            evidence,
            RollbackDataPlaneDisposition.ManualInterventionRequired,
            identityProven ? ReasonFunctionalQueryUnproven : ReasonReadinessUnproven,
            identityProven
                ? "The functional query was not proven before the rollback window elapsed."
                : "The restored revision identity was not proven before the rollback window elapsed.");
    }

    /// <summary>Maps a decision onto the workflow status a backend may return.</summary>
    public static WorkflowOperationStatus ToStatus(RollbackDataPlaneDisposition disposition)
        => disposition switch
        {
            RollbackDataPlaneDisposition.RolledBack => WorkflowOperationStatus.RolledBack,
            RollbackDataPlaneDisposition.Failed => WorkflowOperationStatus.Failed,
            RollbackDataPlaneDisposition.ManualInterventionRequired => WorkflowOperationStatus.ManualInterventionRequired,
            _ => WorkflowOperationStatus.RollbackRequested
        };

    /// <summary>Maps a decision onto the observation record backends return.</summary>
    public static DeployObservation ToDeployObservation(RollbackDataPlaneDecision decision, string? providerOperationId)
    {
        ArgumentNullException.ThrowIfNull(decision);
        return new DeployObservation
        {
            Status = ToStatus(decision.Disposition),
            ProviderOperationId = providerOperationId,
            ObservedRevision = decision.ObservedRevision,
            Message = decision.Message
        };
    }

    /// <summary>Reads the rollback observation clock stamped by the reconciler, if present.</summary>
    public static DateTimeOffset? ReadObservationStartedAt(IReadOnlyDictionary<string, string> parameters)
        => parameters.TryGetValue(ObservationStartedAtParameterKey, out var raw) &&
           DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var startedAt)
            ? startedAt
            : null;

    /// <summary>Reads the rollback observation window, clamped to <see cref="MaximumWindow"/>.</summary>
    public static TimeSpan ReadObservationWindow(IReadOnlyDictionary<string, string> parameters)
    {
        if (parameters.TryGetValue(ObservationWindowSecondsParameterKey, out var raw) &&
            double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) &&
            seconds > 0)
        {
            return TimeSpan.FromSeconds(Math.Min(seconds, MaximumWindow.TotalSeconds));
        }

        return DefaultWindow;
    }

    /// <summary>Reads the functional-query expectation. Unconfigured expectations cannot complete a rollback.</summary>
    public static RollbackFunctionalQueryExpectation ReadFunctionalExpectation(IReadOnlyDictionary<string, string> parameters)
        => new()
        {
            Url = ReadParameter(parameters, FunctionalQueryUrlParameterKey),
            Path = ReadParameter(parameters, FunctionalQueryPathParameterKey),
            ExpectedContains = ReadParameter(parameters, FunctionalQueryExpectedContainsParameterKey)
                ?? ReadParameter(parameters, LocalFunctionalQueryExpectedContainsParameterKey),
            ForbiddenContains = ReadParameter(parameters, FunctionalQueryForbiddenContainsParameterKey)
                ?? ReadParameter(parameters, LocalFunctionalQueryForbiddenContainsParameterKey),
            ExpectedSha256 = ReadParameter(parameters, FunctionalQueryExpectedSha256ParameterKey)
                ?? ReadParameter(parameters, LocalFunctionalQueryExpectedSha256ParameterKey),
            ExpectedStatusCode = ReadInt(parameters, FunctionalQueryExpectedStatusParameterKey, 100, 599) ?? 200,
            TimeoutSeconds = ReadInt(parameters, FunctionalQueryTimeoutSecondsParameterKey, 1, 60) ?? 5
        };

    /// <summary>
    /// Runs the configured functional query. Returns <see cref="RollbackFunctionalQueryVerdict.NotProven"/>
    /// when the expectation or the probe is missing, and <see cref="RollbackFunctionalQueryVerdict.Unreachable"/>
    /// when the probe throws.
    /// </summary>
    public static async Task<RollbackFunctionalQueryVerdict> ProbeFunctionalQueryAsync(
        IRollbackDataPlaneProbe? probe,
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken cancellationToken)
    {
        var expectation = ReadFunctionalExpectation(parameters);
        if (!expectation.IsConfigured || string.IsNullOrWhiteSpace(expectation.Url) || probe is null)
        {
            return RollbackFunctionalQueryVerdict.NotProven;
        }

        try
        {
            var result = await probe.ProbeFunctionalQueryAsync(
                    new RollbackFunctionalProbeRequest
                    {
                        Url = expectation.Url,
                        ExpectedContains = expectation.ExpectedContains,
                        ForbiddenContains = expectation.ForbiddenContains,
                        ExpectedSha256 = expectation.ExpectedSha256,
                        ExpectedStatusCode = expectation.ExpectedStatusCode,
                        TimeoutSeconds = expectation.TimeoutSeconds
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            return result.Verdict;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return RollbackFunctionalQueryVerdict.Unreachable;
        }
    }

    /// <summary>
    /// Runs the configured HTTP readiness probe. Null means the URL is not configured.
    /// False means the probe ran and the endpoint was not ready, or the probe is missing.
    /// </summary>
    public static async Task<bool?> ProbeReadinessAsync(
        IRollbackDataPlaneProbe? probe,
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken cancellationToken)
    {
        var url = ReadParameter(parameters, ReadinessUrlParameterKey);
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        if (probe is null)
        {
            return false;
        }

        try
        {
            var result = await probe.ProbeReadinessAsync(
                    new RollbackReadinessProbeRequest
                    {
                        Url = url,
                        ExpectedStatusCode = ReadInt(parameters, ReadinessExpectedStatusParameterKey, 100, 599) ?? 200,
                        TimeoutSeconds = ReadInt(parameters, ReadinessTimeoutSecondsParameterKey, 1, 60) ?? 5
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            return result.Proven;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
    }

    private static bool IsWindowExpired(RollbackDataPlaneEvidence evidence, DateTimeOffset utcNow)
    {
        if (evidence.RollbackStartedAt is not { } started)
        {
            return false;
        }

        var window = evidence.Window <= TimeSpan.Zero ? DefaultWindow : evidence.Window;
        if (window > MaximumWindow)
        {
            window = MaximumWindow;
        }

        return utcNow >= started + window;
    }

    private static RollbackDataPlaneDecision Decide(
        RollbackDataPlaneEvidence evidence,
        RollbackDataPlaneDisposition disposition,
        string reasonCode,
        string summary)
    {
        var serving = string.IsNullOrWhiteSpace(evidence.ServingRevision) ? "<none>" : evidence.ServingRevision;
        return new RollbackDataPlaneDecision
        {
            Disposition = disposition,
            ObservedRevision = evidence.ServingRevision,
            ReasonCode = reasonCode,
            Message = summary +
                $" reason={reasonCode}; servingRevision={serving}; healthy={evidence.HealthyEndpointCount.ToString(CultureInfo.InvariantCulture)}; registered={evidence.RegisteredEndpointCount.ToString(CultureInfo.InvariantCulture)}; functionalQuery={evidence.FunctionalQuery}."
        };
    }

    private static string? ReadParameter(IReadOnlyDictionary<string, string> parameters, string key)
        => parameters.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;

    private static int? ReadInt(IReadOnlyDictionary<string, string> parameters, string key, int minimum, int maximum)
    {
        var raw = ReadParameter(parameters, key);
        if (raw is null || !int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return null;
        }

        return parsed >= minimum && parsed <= maximum ? parsed : null;
    }
}
