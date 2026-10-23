// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

internal static class RollbackDataPlaneTestSupport
{
    public const string PriorMarker = "marker:revision-prior";
    public const string CandidateMarker = "marker:revision-candidate";

    public static void AddProof(IDictionary<string, string> parameters)
    {
        parameters[RollbackDataPlaneCompletion.ReadinessUrlParameterKey] = "https://probe.example/healthz";
        parameters[RollbackDataPlaneCompletion.FunctionalQueryUrlParameterKey] = "https://probe.example/golden";
        parameters[RollbackDataPlaneCompletion.FunctionalQueryExpectedContainsParameterKey] = PriorMarker;
        parameters[RollbackDataPlaneCompletion.FunctionalQueryForbiddenContainsParameterKey] = CandidateMarker;
    }

    public static void StampExpiredWindow(IDictionary<string, string> parameters)
    {
        parameters[RollbackDataPlaneCompletion.ObservationStartedAtParameterKey] =
            DateTimeOffset.UtcNow.AddMinutes(-10).ToString("O");
        parameters[RollbackDataPlaneCompletion.ObservationWindowSecondsParameterKey] = "60";
    }

    public static ConfigurableRollbackDataPlaneProbe HealthyProbe()
        => new();
}

internal sealed class ConfigurableRollbackDataPlaneProbe : IRollbackDataPlaneProbe
{
    public bool ReadinessProven { get; set; } = true;

    public RollbackFunctionalQueryVerdict FunctionalQuery { get; set; } =
        RollbackFunctionalQueryVerdict.MatchedPriorMarker;

    public int QueryCalls { get; private set; }

    public Task<RollbackReadinessProbeResult> ProbeReadinessAsync(
        RollbackReadinessProbeRequest request,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new RollbackReadinessProbeResult { Proven = ReadinessProven });

    public Task<RollbackFunctionalProbeResult> ProbeFunctionalQueryAsync(
        RollbackFunctionalProbeRequest request,
        CancellationToken cancellationToken = default)
    {
        QueryCalls++;
        return Task.FromResult(new RollbackFunctionalProbeResult { Verdict = FunctionalQuery });
    }
}
