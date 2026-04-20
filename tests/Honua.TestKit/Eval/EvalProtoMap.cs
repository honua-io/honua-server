// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;
using Proto = Geospatial.V1;

namespace Honua.TestKit.Eval;

/// <summary>
/// Stateless enum conversions between the geoprocessing domain types and the
/// <see cref="Proto"/> messages. Mirrors the server-internal conversion helpers
/// so the eval harness can round-trip plans without pulling in server internals.
/// </summary>
internal static class EvalProtoMap
{
    /// <summary>
    /// Maps a proto <see cref="Proto.ArtifactKind"/> to the domain enum, or returns
    /// <c>null</c> when the proto enum has no domain counterpart. Callers record the
    /// unknown value as a deterministic stage failure rather than throwing so the
    /// eval report is always emitted.
    /// </summary>
    public static ArtifactKind? ToDomainArtifactKind(Proto.ArtifactKind kind) => kind switch
    {
        Proto.ArtifactKind.Scalar => ArtifactKind.Scalar,
        Proto.ArtifactKind.FeatureLayer => ArtifactKind.FeatureLayer,
        Proto.ArtifactKind.Table => ArtifactKind.Table,
        Proto.ArtifactKind.Raster => ArtifactKind.Raster,
        Proto.ArtifactKind.File => ArtifactKind.File,
        Proto.ArtifactKind.Report => ArtifactKind.Report,
        Proto.ArtifactKind.Map => ArtifactKind.Map,
        Proto.ArtifactKind.AppBundle => ArtifactKind.AppBundle,
        _ => null
    };

    /// <summary>Maps a domain <see cref="ArtifactKind"/> to the proto enum.</summary>
    public static Proto.ArtifactKind ToProtoArtifactKind(ArtifactKind kind) => kind switch
    {
        ArtifactKind.Scalar => Proto.ArtifactKind.Scalar,
        ArtifactKind.FeatureLayer => Proto.ArtifactKind.FeatureLayer,
        ArtifactKind.Table => Proto.ArtifactKind.Table,
        ArtifactKind.Raster => Proto.ArtifactKind.Raster,
        ArtifactKind.File => Proto.ArtifactKind.File,
        ArtifactKind.Report => Proto.ArtifactKind.Report,
        ArtifactKind.Map => Proto.ArtifactKind.Map,
        ArtifactKind.AppBundle => Proto.ArtifactKind.AppBundle,
        _ => Proto.ArtifactKind.Unspecified
    };

    /// <summary>Maps a domain <see cref="AnalysisPlanStepKind"/> to the proto enum.</summary>
    public static Proto.PlanStepKind ToProtoPlanStepKind(AnalysisPlanStepKind kind) => kind switch
    {
        AnalysisPlanStepKind.QueryFeatures => Proto.PlanStepKind.QueryFeatures,
        AnalysisPlanStepKind.Geoprocess => Proto.PlanStepKind.Geoprocess,
        AnalysisPlanStepKind.Aggregate => Proto.PlanStepKind.Aggregate,
        AnalysisPlanStepKind.RenderMap => Proto.PlanStepKind.RenderMap,
        AnalysisPlanStepKind.Export => Proto.PlanStepKind.Export,
        _ => Proto.PlanStepKind.Unspecified
    };
}
