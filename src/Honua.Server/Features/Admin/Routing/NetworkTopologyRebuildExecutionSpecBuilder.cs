// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Server.Features.Admin.Routing;

internal sealed record NetworkTopologyRebuildJobRequest(
    string DatasetId,
    long Generation,
    long Attempt,
    long ExpectedSourceRevision,
    int Srid);

internal static class NetworkTopologyRebuildExecutionSpecBuilder
{
    private const string DatasetIdKey = "datasetId";
    private const string GenerationKey = "generation";
    private const string AttemptKey = "attempt";
    private const string ExpectedSourceRevisionKey = "expectedSourceRevision";
    private const string SridKey = "srid";

    public static ExecutionJobSpec Build(NetworkTopologyRebuildJobRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new ExecutionJobSpec
        {
            Kind = ExecutionJobKind.NetworkTopologyRebuild,
            TargetKind = BatchComputeTargetKind.LocalProcess,
            Backend = "local",
            WorkloadName = $"topology-rebuild:{request.DatasetId}:{request.Generation}:{request.Attempt}",
            Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [DatasetIdKey] = request.DatasetId,
                [GenerationKey] = request.Generation.ToString(CultureInfo.InvariantCulture),
                [AttemptKey] = request.Attempt.ToString(CultureInfo.InvariantCulture),
                [ExpectedSourceRevisionKey] = request.ExpectedSourceRevision.ToString(CultureInfo.InvariantCulture),
                [SridKey] = request.Srid.ToString(CultureInfo.InvariantCulture),
            },
        };
    }

    public static bool TryParse(
        IReadOnlyDictionary<string, string> parameters,
        out NetworkTopologyRebuildJobRequest request,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        request = null!;
        error = string.Empty;

        if (!parameters.TryGetValue(DatasetIdKey, out var datasetId) || string.IsNullOrWhiteSpace(datasetId))
        {
            error = $"'{DatasetIdKey}' is required.";
            return false;
        }

        if (!parameters.TryGetValue(GenerationKey, out var generationRaw) ||
            !long.TryParse(generationRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var generation))
        {
            error = $"'{GenerationKey}' is required and must be an integer.";
            return false;
        }

        if (!parameters.TryGetValue(AttemptKey, out var attemptRaw) ||
            !long.TryParse(attemptRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var attempt))
        {
            error = $"'{AttemptKey}' is required and must be an integer.";
            return false;
        }

        if (!parameters.TryGetValue(ExpectedSourceRevisionKey, out var revisionRaw) ||
            !long.TryParse(revisionRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var expectedSourceRevision))
        {
            error = $"'{ExpectedSourceRevisionKey}' is required and must be an integer.";
            return false;
        }

        if (!parameters.TryGetValue(SridKey, out var sridRaw) ||
            !int.TryParse(sridRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var srid))
        {
            error = $"'{SridKey}' is required and must be an integer.";
            return false;
        }

        request = new NetworkTopologyRebuildJobRequest(datasetId, generation, attempt, expectedSourceRevision, srid);
        return true;
    }
}
