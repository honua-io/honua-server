// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Server.Features.Admin.Routing;

/// <summary>
/// Provider-neutral payload for one shadow-topology rebuild execution job (#2718),
/// encoded onto/decoded from an <see cref="ExecutionJobSpec"/>.
/// </summary>
/// <param name="DatasetId">Stable network-dataset identifier.</param>
/// <param name="Generation">Topology generation to rebuild.</param>
/// <param name="Attempt">Rebuild attempt number.</param>
/// <param name="ExpectedSourceRevision">Source revision the attempt is fenced against.</param>
/// <param name="Srid">Spatial reference the shadow topology must be built in.</param>
internal sealed record NetworkTopologyRebuildJobRequest(
    string DatasetId,
    long Generation,
    long Attempt,
    long ExpectedSourceRevision,
    int Srid);

/// <summary>
/// Translates a <see cref="NetworkTopologyRebuildJobRequest"/> into a durable
/// <see cref="ExecutionJobSpec"/> of kind <see cref="ExecutionJobKind.NetworkTopologyRebuild"/>
/// and back, keeping the encode/decode contract in one place (mirroring
/// <c>RouterBuildExecutionSpecBuilder</c>) so the admin submission path and the in-process
/// worker stay in agreement. Pure and infrastructure-free, so it is directly unit-testable.
/// </summary>
internal static class NetworkTopologyRebuildExecutionSpecBuilder
{
    private const string DatasetIdKey = "datasetId";
    private const string GenerationKey = "generation";
    private const string AttemptKey = "attempt";
    private const string ExpectedSourceRevisionKey = "expectedSourceRevision";
    private const string SridKey = "srid";

    /// <summary>
    /// Builds the execution-job spec for <paramref name="request"/>. Runs entirely
    /// in-process against Postgres (no remote batch backend), so <see cref="ExecutionJobSpec.Backend"/>
    /// is always <c>local</c>.
    /// </summary>
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

    /// <summary>
    /// Reconstructs a <see cref="NetworkTopologyRebuildJobRequest"/> from a rebuild
    /// execution job's spec parameters. Returns <see langword="false"/> with a clean
    /// classification when a required key is absent or malformed so the worker can fail the
    /// job without throwing.
    /// </summary>
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
