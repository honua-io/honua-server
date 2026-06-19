// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Server.Features.Provisioner.BuildJobs;

/// <summary>
/// Translates a <see cref="RouterBuildRequest"/> into a durable
/// <see cref="ExecutionJobSpec"/> of kind <see cref="ExecutionJobKind.RouterBuild"/> and
/// back, keeping the encode/decode contract in one place (mirroring
/// <c>TileCacheExecutionSpecBuilder</c>) so the admin submission path and the GP-on-Batch
/// worker stay in agreement. Pure and allocation-light; no infrastructure dependencies so
/// it is directly unit-testable.
/// </summary>
internal static class RouterBuildExecutionSpecBuilder
{
    /// <summary>
    /// Builds the execution-job spec for <paramref name="request"/>, encoding every
    /// behavior-changing build parameter onto <see cref="ExecutionJobSpec.Parameters"/>.
    /// </summary>
    public static ExecutionJobSpec Build(RouterBuildRequest request, ProvisionerBuildBatchOptions options)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);

        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ProvisionerBuildJobParameterKeys.SourceId] = request.SourceId,
            [ProvisionerBuildJobParameterKeys.ProductId] = request.ProductId,
            [ProvisionerBuildJobParameterKeys.Area] = request.Area.ToParameterValue(),
            [ProvisionerBuildJobParameterKeys.FeedstockTable] = request.FeedstockTable,
            [ProvisionerBuildJobParameterKeys.SchemaName] = request.SchemaName,
            [ProvisionerBuildJobParameterKeys.ArtifactName] = request.ArtifactName,
            [ProvisionerBuildJobParameterKeys.ArtifactKey] = request.ArtifactKey,
            [ProvisionerBuildJobParameterKeys.GraphKind] = request.GraphKind,
        };

        foreach (var kv in options.Parameters)
        {
            parameters[kv.Key] = kv.Value;
        }

        return new ExecutionJobSpec
        {
            Kind = ExecutionJobKind.RouterBuild,
            TargetKind = options.TargetKind,
            Backend = options.Backend,
            WorkloadName = $"router-build:{request.ArtifactName}:{request.Area.ToParameterValue()}",
            Artifact = options.RouterArtifact ?? options.Artifact,
            RuntimeProfile = options.RuntimeProfile,
            Parameters = parameters,
        };
    }

    /// <summary>
    /// Reconstructs a <see cref="RouterBuildRequest"/> from a router-build execution job's
    /// spec parameters. Returns <c>false</c> with a clean classification when a required
    /// key is absent or malformed so the worker can fail the job without throwing.
    /// </summary>
    public static bool TryParse(
        IReadOnlyDictionary<string, string> parameters,
        out RouterBuildRequest request,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        request = null!;
        error = string.Empty;

        if (!ProvisionerBuildSpecHelper.TryReadCommon(
                parameters,
                out var sourceId,
                out var productId,
                out var area,
                out var feedstockTable,
                out var schemaName,
                out var artifactName,
                out var artifactKey,
                out error))
        {
            return false;
        }

        var graphKind = parameters.TryGetValue(ProvisionerBuildJobParameterKeys.GraphKind, out var gk)
            && !string.IsNullOrWhiteSpace(gk)
            ? gk
            : RouterArtifactKinds.PgRoutingTopology;

        request = new RouterBuildRequest
        {
            SourceId = sourceId,
            ProductId = productId,
            Area = area,
            FeedstockTable = feedstockTable,
            SchemaName = schemaName,
            ArtifactName = artifactName,
            ArtifactKey = artifactKey,
            GraphKind = graphKind,
        };

        return true;
    }
}
