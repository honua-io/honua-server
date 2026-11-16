// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;

namespace Honua.Core.Features.ControlPlane.Domain;

public enum RollbackFunctionalQueryVerdict
{
    NotProven = 0,

    MatchedPriorMarker = 1,

    ServedOtherMarker = 2,

    ServedErrorEnvelope = 3,

    Unreachable = 4
}

public enum RollbackDataPlaneDisposition
{
    StillSettling = 0,

    AwaitingDataPlane = 1,

    RolledBack = 2,

    Failed = 3,

    ManualInterventionRequired = 4
}

public sealed record RollbackDataPlaneEvidence
{
    public bool RoutingConverged { get; init; }

    public bool PriorRevisionIdentityProven { get; init; }

    public bool ServingIdentityMismatched { get; init; }

    public string? ServingRevision { get; init; }

    public int HealthyEndpointCount { get; init; }

    public int RegisteredEndpointCount { get; init; }

    public RollbackFunctionalQueryVerdict FunctionalQuery { get; init; }

    public DateTimeOffset? RollbackStartedAt { get; init; }

    public TimeSpan Window { get; init; }
}

public sealed record RollbackDataPlaneDecision
{
    public required RollbackDataPlaneDisposition Disposition { get; init; }

    public string? ObservedRevision { get; init; }

    public required string ReasonCode { get; init; }

    public required string Message { get; init; }
}

public sealed record RollbackFunctionalQueryExpectation
{
    public string? Url { get; init; }

    public string? Path { get; init; }

    public string? ExpectedContains { get; init; }

    public string? ForbiddenContains { get; init; }

    public string? ExpectedSha256 { get; init; }

    public int ExpectedStatusCode { get; init; } = 200;

    public int TimeoutSeconds { get; init; } = 5;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ExpectedContains) || !string.IsNullOrWhiteSpace(ExpectedSha256);
}

public sealed record RollbackReadinessProbeRequest
{
    public required string Url { get; init; }

    public int ExpectedStatusCode { get; init; } = 200;

    public int TimeoutSeconds { get; init; } = 5;
}

public sealed record RollbackReadinessProbeResult
{
    public bool Proven { get; init; }

    public string? Detail { get; init; }
}

public sealed record RollbackFunctionalProbeRequest
{
    public required string Url { get; init; }

    public string? ExpectedContains { get; init; }

    public string? ForbiddenContains { get; init; }

    public string? ExpectedSha256 { get; init; }

    public int ExpectedStatusCode { get; init; } = 200;

    public int TimeoutSeconds { get; init; } = 5;
}

public sealed record RollbackFunctionalProbeResult
{
    public required RollbackFunctionalQueryVerdict Verdict { get; init; }

    public string? Detail { get; init; }
}

public interface IRollbackDataPlaneProbe
{
    Task<RollbackReadinessProbeResult> ProbeReadinessAsync(
        RollbackReadinessProbeRequest request,
        CancellationToken cancellationToken = default);

    Task<RollbackFunctionalProbeResult> ProbeFunctionalQueryAsync(
        RollbackFunctionalProbeRequest request,
        CancellationToken cancellationToken = default);
}

public static class RollbackDataPlaneCompletion
{
    public const string ObservationStartedAtParameterKey = "deployment.rollback.observation_started_at";

    public const string ObservationWindowSecondsParameterKey = "deployment.rollback.observation_timeout_seconds";

    public const string ReadinessUrlParameterKey = "telemetry.healthz.url";

    public const string ReadinessExpectedStatusParameterKey = "telemetry.healthz.expected_status";

    public const string ReadinessTimeoutSecondsParameterKey = "telemetry.healthz.timeout_seconds";

    public const string FunctionalQueryUrlParameterKey = "telemetry.golden_query.url";

    public const string FunctionalQueryPathParameterKey = "rollback.functional_query.path";

    public const string LocalFunctionalQueryExpectedContainsParameterKey = "rollback.functional_query.expected_contains";

    public const string LocalFunctionalQueryForbiddenContainsParameterKey = "rollback.functional_query.forbidden_contains";

    public const string LocalFunctionalQueryExpectedSha256ParameterKey = "rollback.functional_query.expected_sha256";

    public const string FunctionalQueryExpectedContainsParameterKey = "telemetry.golden_query.expected_contains";

    public const string FunctionalQueryForbiddenContainsParameterKey = "telemetry.golden_query.forbidden_contains";

    public const string FunctionalQueryExpectedSha256ParameterKey = "telemetry.golden_query.expected_sha256";

    public const string FunctionalQueryExpectedStatusParameterKey = "telemetry.golden_query.expected_status";

    public const string FunctionalQueryTimeoutSecondsParameterKey = "telemetry.golden_query.timeout_seconds";

    public const string ReasonRoutingNotConverged = "rollback.routing_not_converged";

    public const string ReasonIdentityMismatch = "rollback.identity_mismatch";

    public const string ReasonAwaitingDataPlane = "rollback.awaiting_data_plane";

    public const string ReasonRolledBack = "rollback.data_plane_proven";

    public const string ReasonReadinessUnproven = "rollback.readiness_unproven";

    public const string ReasonFunctionalQueryFailed = "rollback.functional_query_failed";

    public const string ReasonFunctionalQueryUnproven = "rollback.functional_query_unproven";

    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(5);

    public static readonly TimeSpan MaximumWindow = TimeSpan.FromMinutes(30);

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

    public static WorkflowOperationStatus ToStatus(RollbackDataPlaneDisposition disposition)
        => disposition switch
        {
            RollbackDataPlaneDisposition.RolledBack => WorkflowOperationStatus.RolledBack,
            RollbackDataPlaneDisposition.Failed => WorkflowOperationStatus.Failed,
            RollbackDataPlaneDisposition.ManualInterventionRequired => WorkflowOperationStatus.ManualInterventionRequired,
            _ => WorkflowOperationStatus.RollbackRequested
        };

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

    public static DateTimeOffset? ReadObservationStartedAt(IReadOnlyDictionary<string, string> parameters)
        => parameters.TryGetValue(ObservationStartedAtParameterKey, out var raw) &&
           DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var startedAt)
            ? startedAt
            : null;

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
