// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;

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

    /// <summary>
    /// The runtime profile a job for this process must run under. Defaults to
    /// <see cref="RuntimeProfiles.Managed"/> so the lean serving image executes it.
    /// Processes backed by the heavyweight out-of-process GDAL worker (the
    /// <c>gdal.*</c> family) declare <see cref="RuntimeProfiles.Native"/>; the
    /// geoprocessing submit path reads this value to stamp
    /// <c>ExecutionJobSpec.RuntimeProfile</c> so the claim fence routes the job to
    /// the correct worker (the lean dispatcher never claims a native job; the GDAL
    /// worker only claims native jobs). This is the data-driven seam that keeps the
    /// routing decision in the catalog rather than hard-coded in the submit path.
    /// </summary>
    public string RuntimeProfile { get; init; } = RuntimeProfiles.Managed;
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

    /// <summary>
    /// Optional finite enumeration of accepted values. When populated, protocol
    /// adapters surface it as a choice list (GPServer <c>choiceList</c>, OGC
    /// JSON Schema <c>enum</c>) and reject inbound values that fall outside the
    /// set. Comparison is case-insensitive at the adapter boundary so callers
    /// can match ArcGIS-style mixed-case strings.
    /// </summary>
    public IReadOnlyList<string>? AllowedValues { get; init; }
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
