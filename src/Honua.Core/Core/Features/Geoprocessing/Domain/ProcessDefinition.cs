// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Core.Features.Geoprocessing.Domain;

public sealed record ProcessDefinition
{
    public required string ProcessId { get; init; }

    public required string Title { get; init; }

    public required string Description { get; init; }

    public required string Category { get; init; }

    public required IReadOnlyList<ProcessParameterSpec> Parameters { get; init; }

    public required IReadOnlyList<ArtifactKind> OutputArtifactKinds { get; init; }

    public bool OutputsAreAlternatives { get; init; }

    public ProcessExecutionTier ExecutionTier { get; init; } = ProcessExecutionTier.Analytic;

    public string RuntimeProfile { get; init; } = RuntimeProfiles.Managed;

    public ProcessExecutionKind ExecutionKind { get; init; } = ProcessExecutionKind.Unclassified;

    public ProcessExecutionModes SupportedExecutionModes { get; init; } = ProcessExecutionModes.None;

    public ProcessEntryPoints SupportedEntryPoints { get; init; } = ProcessEntryPoints.None;

    public string? ConfigurationDependency { get; init; }

    public string? ExecutionCapabilityReason { get; init; }
}

public enum ProcessExecutionKind
{
    Unclassified,

    Job,

    ProtocolOnly,

    WorkflowOnly,

    Unavailable
}

[Flags]
public enum ProcessEntryPoints
{
    None = 0,

    Job = 1,

    Protocol = 2,

    Workflow = 4
}

[Flags]
public enum ProcessExecutionModes
{
    None = 0,

    Async = 1,

    Sync = 2
}

public enum ProcessExecutionTier
{
    Analytic,

    Mutating
}

public sealed record ProcessParameterSpec
{
    public required string Name { get; init; }

    public required string DisplayName { get; init; }

    public required string Description { get; init; }

    public required ProcessParameterValueType ValueType { get; init; }

    public bool Required { get; init; }

    public string? DefaultValue { get; init; }

    public IReadOnlyList<string>? AllowedValues { get; init; }

    public bool AcceptsRasterSource { get; init; }

    public bool AcceptsGeoJsonDataUri { get; init; }

    public bool IsAuthorizationSelector { get; init; }

    public ProcessLayerAccess LayerAccess { get; init; } = ProcessLayerAccess.Read;
}

public enum ProcessLayerAccess
{
    None,

    Read,

    Insert,

    Update,

    Delete,
}

public enum ProcessParameterValueType
{
    Text,

    WholeNumber,

    FloatingPoint,

    Flag,

    Wkb,

    WkbArray,

    Srid,

    LayerId
}
