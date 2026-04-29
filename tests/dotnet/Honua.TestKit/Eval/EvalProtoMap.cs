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
    /// Maps a proto <see cref="Proto.ArtifactClass"/> to the domain enum, or returns
    /// <c>null</c> when the proto enum has no domain counterpart. Callers record the
    /// unknown value as a deterministic stage failure rather than throwing so the
    /// eval report is always emitted.
    /// </summary>
    public static ArtifactKind? ToDomainArtifactKind(Proto.ArtifactClass kind) => kind switch
    {
        Proto.ArtifactClass.Scalar => ArtifactKind.Scalar,
        Proto.ArtifactClass.FeatureLayer => ArtifactKind.FeatureLayer,
        Proto.ArtifactClass.Table => ArtifactKind.Table,
        Proto.ArtifactClass.Raster => ArtifactKind.Raster,
        Proto.ArtifactClass.File => ArtifactKind.File,
        Proto.ArtifactClass.Report => ArtifactKind.Report,
        Proto.ArtifactClass.Map => ArtifactKind.Map,
        Proto.ArtifactClass.AppBundle => ArtifactKind.AppBundle,
        _ => null
    };

    /// <summary>Maps a domain <see cref="ArtifactKind"/> to the canonical output token.</summary>
    public static string ToProtoArtifactKind(ArtifactKind kind) => kind switch
    {
        ArtifactKind.Scalar => "scalar",
        ArtifactKind.FeatureLayer => "feature_layer",
        ArtifactKind.Table => "table",
        ArtifactKind.Raster => "raster",
        ArtifactKind.File => "file",
        ArtifactKind.Report => "report",
        ArtifactKind.Map => "map",
        ArtifactKind.AppBundle => "app_bundle",
        _ => ""
    };

    /// <summary>Maps a domain <see cref="AnalysisPlanStepKind"/> to the canonical step kind token.</summary>
    public static string ToProtoPlanStepKind(AnalysisPlanStepKind kind) => kind switch
    {
        AnalysisPlanStepKind.QueryFeatures => "query_features",
        AnalysisPlanStepKind.Geoprocess => "geoprocess",
        AnalysisPlanStepKind.Aggregate => "aggregate",
        AnalysisPlanStepKind.RenderMap => "render_map",
        AnalysisPlanStepKind.Export => "export",
        _ => ""
    };

    /// <summary>Maps a string input into the canonical parameter value envelope.</summary>
    public static Proto.ParameterValue ToProtoParameterValue(string value)
        => new() { StringValue = value };
}
