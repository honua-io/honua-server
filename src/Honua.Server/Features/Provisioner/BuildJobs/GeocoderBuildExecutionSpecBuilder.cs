// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Server.Features.Provisioner.BuildJobs;

/// <summary>
/// Translates a <see cref="GeocoderBuildRequest"/> into a durable
/// <see cref="ExecutionJobSpec"/> of kind <see cref="ExecutionJobKind.GeocoderBuild"/>
/// and back, keeping the encode/decode contract in one place (mirroring
/// <c>TileCacheExecutionSpecBuilder</c>) so the admin submission path and the GP-on-Batch
/// worker stay in agreement. Pure and allocation-light; no infrastructure dependencies so
/// it is directly unit-testable.
/// </summary>
internal static class GeocoderBuildExecutionSpecBuilder
{
    /// <summary>
    /// Builds the execution-job spec for <paramref name="request"/>, encoding every
    /// behavior-changing build parameter onto <see cref="ExecutionJobSpec.Parameters"/>.
    /// </summary>
    public static ExecutionJobSpec Build(GeocoderBuildRequest request, ProvisionerBuildBatchOptions options)
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
            [ProvisionerBuildJobParameterKeys.LocatorKind] = request.LocatorKind,
        };

        // Backend-specific coordinates (AWS Batch job-definition/queue ARNs, target
        // artifact bucket) are merged last so an operator can pin them per deployment
        // without changing code. Identical to the tile-cache dispatch contract.
        foreach (var kv in options.Parameters)
        {
            parameters[kv.Key] = kv.Value;
        }

        return new ExecutionJobSpec
        {
            Kind = ExecutionJobKind.GeocoderBuild,
            TargetKind = options.TargetKind,
            Backend = options.Backend,
            WorkloadName = $"geocoder-build:{request.ArtifactName}:{request.Area.ToParameterValue()}",
            Artifact = options.GeocoderArtifact ?? options.Artifact,
            RuntimeProfile = options.RuntimeProfile,
            Parameters = parameters,
        };
    }

    /// <summary>
    /// Reconstructs a <see cref="GeocoderBuildRequest"/> from a geocoder-build execution
    /// job's spec parameters. Returns <c>false</c> with a clean classification when a
    /// required key is absent or malformed so the worker can fail the job without throwing.
    /// </summary>
    public static bool TryParse(
        IReadOnlyDictionary<string, string> parameters,
        out GeocoderBuildRequest request,
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

        var locatorKind = parameters.TryGetValue(ProvisionerBuildJobParameterKeys.LocatorKind, out var lk)
            && !string.IsNullOrWhiteSpace(lk)
            ? lk
            : GeocoderArtifactKinds.NominatimPbf;

        request = new GeocoderBuildRequest
        {
            SourceId = sourceId,
            ProductId = productId,
            Area = area,
            FeedstockTable = feedstockTable,
            SchemaName = schemaName,
            ArtifactName = artifactName,
            ArtifactKey = artifactKey,
            LocatorKind = locatorKind,
        };

        return true;
    }
}
