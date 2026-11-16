// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;
using Proto = Geospatial.V1;

namespace Honua.TestKit.Eval;

internal static class EvalProtoMap
{
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

    public static string ToProtoPlanStepKind(AnalysisPlanStepKind kind) => kind switch
    {
        AnalysisPlanStepKind.QueryFeatures => "query_features",
        AnalysisPlanStepKind.Geoprocess => "geoprocess",
        AnalysisPlanStepKind.Aggregate => "aggregate",
        AnalysisPlanStepKind.RenderMap => "render_map",
        AnalysisPlanStepKind.Export => "export",
        _ => ""
    };

    public static Proto.ParameterValue ToProtoParameterValue(string value)
        => new() { StringValue = value };
}
