// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Raster;

namespace Honua.Core.Features.Geoprocessing.Domain;

public sealed record AnalysisPlan
{
    public required string PlanId { get; init; }

    public required string IntentId { get; init; }

    public required IReadOnlyList<AnalysisPlanStep> Steps { get; init; }

    public IReadOnlyList<ArtifactKind> Outputs { get; init; } = [];

    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed record AnalysisPlanStep
{
    public required string StepId { get; init; }

    public required AnalysisPlanStepKind Kind { get; init; }

    public string? ProcessId { get; init; }

    public IReadOnlyDictionary<string, string> Inputs { get; init; } = new Dictionary<string, string>();

    // A setter preserves this default when .NET 10 source-generated JSON omits the
    // optional property. An init-only member is assigned null even when absent
    // (dotnet/runtime#84484). Explicit JSON null must still reach validation.
    public IReadOnlyDictionary<string, RasterSourceDescriptor> RasterSources { get; set; } =
        new Dictionary<string, RasterSourceDescriptor>();

    public IReadOnlyList<string> DependsOn { get; init; } = [];
}
