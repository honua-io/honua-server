// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Shared.Models;

namespace Honua.Core.Features.Migration.Domain;

public sealed record LayerReconciliationRequest
{
    public required string RunId { get; init; }

    public required string SourceKind { get; init; }

    public IReadOnlyList<LayerReconciliationLayerInput> Layers { get; init; } = [];

    public LayerReconciliationOptions Options { get; init; } = new();
}

public sealed record LayerReconciliationLayerInput
{
    public required string SourceLayerId { get; init; }

    public string? SourceLayerName { get; init; }

    public required int? TargetHonuaLayerId { get; init; }

    public long? SourceFeatureCount { get; init; }

    public bool SourceHasGeometry { get; init; } = true;

    public BoundingBox? SourceExtent { get; init; }

    public int? PlannedTargetSrid { get; init; }

    public IReadOnlyList<string> SourceFieldNames { get; init; } = [];

    public string? FilterMirror { get; init; }

    public bool TargetContainsOnlyImportedFeatures { get; init; }

    public SourceGeometryCensus? SourceGeometry { get; init; }

    public BoundingBox? QueriedSourceExtent { get; init; }

    public IReadOnlyDictionary<string, string>? FilterFieldMappings { get; init; }
}

public sealed record SourceGeometryCensus
{
    public IReadOnlySet<long> AbsentTargetFeatureIds { get; init; } = new HashSet<long>();

    public IReadOnlySet<long> UnconvertedTargetFeatureIds { get; init; } = new HashSet<long>();

    public IReadOnlySet<long> InvalidTargetFeatureIds { get; init; } = new HashSet<long>();
}

public sealed record LayerReconciliationOptions
{
    public const int DefaultSampleSize = 100;

    public const double DefaultCountWarnRatio = 0.05;

    public const double DefaultCountFailRatio = 0.20;

    public const double DefaultGeometryPassRatio = 0.99;

    public const double DefaultGeometryWarnRatio = 0.95;

    public const double DefaultExtentTolerance = 0.001;

    public int SampleSize { get; init; } = DefaultSampleSize;

    public double CountWarnRatio { get; init; } = DefaultCountWarnRatio;

    public double CountFailRatio { get; init; } = DefaultCountFailRatio;

    public double GeometryPassRatio { get; init; } = DefaultGeometryPassRatio;

    public double GeometryWarnRatio { get; init; } = DefaultGeometryWarnRatio;

    public double ExtentTolerance { get; init; } = DefaultExtentTolerance;
}
