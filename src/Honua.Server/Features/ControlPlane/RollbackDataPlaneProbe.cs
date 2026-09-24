// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.ControlPlane;

/// <summary>
/// Production <see cref="IRollbackDataPlaneProbe"/> over the shared deploy health and golden-query probes.
/// </summary>
internal sealed class HttpRollbackDataPlaneProbe(IDeployHealthProbe healthProbe) : IRollbackDataPlaneProbe
{
    public async Task<RollbackReadinessProbeResult> ProbeReadinessAsync(
        RollbackReadinessProbeRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await healthProbe.ProbeAsync(
                new DeployHealthProbeRequest
                {
                    Url = request.Url,
                    Samples = 1,
                    ExpectedStatusCode = request.ExpectedStatusCode,
                    TimeoutSeconds = request.TimeoutSeconds
                },
                cancellationToken)
            .ConfigureAwait(false);
        return new RollbackReadinessProbeResult
        {
            Proven = result.Validated && result.Attempts > 0 && result.Failures == 0,
            Detail = result.Detail
        };
    }

    public async Task<RollbackFunctionalProbeResult> ProbeFunctionalQueryAsync(
        RollbackFunctionalProbeRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await healthProbe.ProbeGoldenQueryAsync(
                new DeployGoldenQueryRequest
                {
                    Url = request.Url,
                    ExpectedSha256 = request.ExpectedSha256,
                    ExpectedBodyContains = request.ExpectedContains,
                    ForbiddenBodyContains = request.ForbiddenContains,
                    ExpectedStatusCode = request.ExpectedStatusCode,
                    TimeoutSeconds = request.TimeoutSeconds
                },
                cancellationToken)
            .ConfigureAwait(false);
        return new RollbackFunctionalProbeResult
        {
            Verdict = Classify(result),
            Detail = result.Detail
        };
    }

    internal static RollbackFunctionalQueryVerdict Classify(DeployGoldenQueryResult result)
    {
        if (!result.Validated)
        {
            return RollbackFunctionalQueryVerdict.NotProven;
        }

        if (result.Matched)
        {
            return RollbackFunctionalQueryVerdict.MatchedPriorMarker;
        }

        var detail = result.Detail ?? string.Empty;
        if (detail.Contains("could not be reached", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("returned status", StringComparison.OrdinalIgnoreCase))
        {
            return RollbackFunctionalQueryVerdict.Unreachable;
        }

        if (detail.Contains("error envelope", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("Unhealthy", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("exception", StringComparison.OrdinalIgnoreCase))
        {
            return RollbackFunctionalQueryVerdict.ServedErrorEnvelope;
        }

        return RollbackFunctionalQueryVerdict.ServedOtherMarker;
    }
}
