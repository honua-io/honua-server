// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Geoprocessing.Domain;

/// <summary>
/// Describes a built-in geoprocessing operation that can be referenced by
/// <see cref="AnalysisPlanStep.ProcessId"/> and discovered through the process catalog.
/// </summary>
public sealed record ProcessDefinition
{
    /// <summary>
    /// Stable dotted identifier (e.g. <c>geometry.buffer</c>, <c>analytics.cluster</c>).
    /// </summary>
    public required string ProcessId { get; init; }

    /// <summary>
    /// Short human-readable title.
    /// </summary>
    public required string Title { get; init; }

    /// <summary>
    /// One-sentence description of what the process does.
    /// </summary>
    public required string Description { get; init; }

    /// <summary>
    /// Top-level category (e.g. <c>geometry</c>, <c>analytics</c>).
    /// </summary>
    public required string Category { get; init; }

    /// <summary>
    /// Ordered parameter specifications for this process.
    /// </summary>
    public required IReadOnlyList<ProcessParameterSpec> Parameters { get; init; }

    /// <summary>
    /// Artifact kinds this process is expected to produce.
    /// </summary>
    public required IReadOnlyList<ArtifactKind> OutputArtifactKinds { get; init; }
}

/// <summary>
/// Describes a single parameter accepted by a <see cref="ProcessDefinition"/>.
/// </summary>
public sealed record ProcessParameterSpec
{
    /// <summary>
    /// Machine-readable parameter name matching the key in <see cref="AnalysisPlanStep.Inputs"/>.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Human-readable label for display.
    /// </summary>
    public required string DisplayName { get; init; }

    /// <summary>
    /// Short description of what this parameter controls.
    /// </summary>
    public required string Description { get; init; }

    /// <summary>
    /// Expected value type for validation and UI hints.
    /// </summary>
    public required ProcessParameterValueType ValueType { get; init; }

    /// <summary>
    /// Whether this parameter must be supplied for the process to execute.
    /// </summary>
    public bool Required { get; init; }

    /// <summary>
    /// Default value used when the parameter is not supplied, serialized as a string.
    /// </summary>
    public string? DefaultValue { get; init; }
}

/// <summary>
/// Value types for process parameters, used for validation and UI rendering hints.
/// </summary>
public enum ProcessParameterValueType
{
    /// <summary>
    /// Free-form text value.
    /// </summary>
    Text,

    /// <summary>
    /// 32-bit signed integer value.
    /// </summary>
    WholeNumber,

    /// <summary>
    /// Double-precision floating-point value.
    /// </summary>
    FloatingPoint,

    /// <summary>
    /// Boolean flag.
    /// </summary>
    Flag,

    /// <summary>
    /// Well-Known Binary geometry.
    /// </summary>
    Wkb,

    /// <summary>
    /// Array of Well-Known Binary geometries.
    /// </summary>
    WkbArray,

    /// <summary>
    /// Spatial Reference Identifier.
    /// </summary>
    Srid,

    /// <summary>
    /// Layer identifier referencing a dataset in the layer catalog.
    /// </summary>
    LayerId
}
