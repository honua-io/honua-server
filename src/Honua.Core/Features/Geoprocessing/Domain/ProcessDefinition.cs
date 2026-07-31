// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;

using Honua.Core.Features.Authorization.Domain;

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
    /// True when <see cref="OutputArtifactKinds"/> lists MUTUALLY EXCLUSIVE output
    /// shapes rather than outputs produced together, so exactly one of them is
    /// emitted per run.
    /// </summary>
    /// <remarks>
    /// Defaults to false, which preserves the "one artifact per declared output"
    /// reading every existing process relies on. It is set only where a process
    /// genuinely chooses between shapes — <c>imagery.classify</c>, whose backend
    /// decides whether a scene yields a classification raster or detected
    /// features. Protocol adapters use it to avoid advertising every alternative
    /// as a guaranteed result: GPServer marks such outputs optional, because a
    /// client enumerating required outputs from task metadata would otherwise wait
    /// for a second artifact that is never produced.
    /// </remarks>
    public bool OutputsAreAlternatives { get; init; }

    /// <summary>
    /// Authorization tier required to execute this process. Analytic processes are available
    /// through the baseline process-execute permission; processes with durable side effects
    /// explicitly opt into <see cref="ProcessExecutionTier.Mutating"/>.
    /// </summary>
    public ProcessExecutionTier ExecutionTier { get; init; } = ProcessExecutionTier.Analytic;

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
/// Classifies the authorization sensitivity of a built-in process execution.
/// </summary>
public enum ProcessExecutionTier
{
    /// <summary>Read-only or analytic execution covered by the baseline process permission.</summary>
    Analytic,

    /// <summary>Execution that imports, mutates, or writes caller-selected durable state.</summary>
    Mutating
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

    /// <summary>
    /// For a <see cref="ProcessParameterValueType.LayerId"/> parameter, what the process does to
    /// the layer — which is what decides the authorization operation the submit-time gate
    /// requires for it. Ignored for every other value type.
    /// </summary>
    /// <remarks>
    /// The submit-time layer gate authorizes each referenced layer for the operation this
    /// declares. Deriving it from the value type alone required <c>Query</c> on every layer
    /// parameter, which refused an import whose caller held the mutating grant but was
    /// deliberately denied read on the destination; collapsing every mutation to a single
    /// "write" then gated <c>delete-features</c> and <c>calculate-field</c> on <c>insert</c>,
    /// which is not the grant either performs (honua-server#3046 review). The default is
    /// <see cref="ProcessLayerAccess.Read"/> so a parameter added later is gated as a read
    /// until someone states otherwise — the conservative direction.
    /// </remarks>
    public ProcessLayerAccess LayerAccess { get; init; } = ProcessLayerAccess.Read;
}

/// <summary>
/// What a process does to a layer named by a <see cref="ProcessParameterValueType.LayerId"/>
/// parameter. Each member names the canonical authorization operation the submit-time gate
/// requires for that layer, so a destructive process is gated on the grant it actually needs
/// rather than on a generic "write".
/// </summary>
/// <remarks>
/// Collapsing every mutation to one member was not sufficient: <c>delete-features</c> and
/// <c>calculate-field</c> would both have demanded <c>insert</c>, which a principal holding
/// only <c>update</c> (or only <c>delete</c>) does not have, and a principal holding
/// <c>insert</c> could invoke either — neither is the grant the operation corresponds to
/// (honua-server#3046 review).
/// </remarks>
public enum ProcessLayerAccess
{
    /// <summary>
    /// The process reads features or raster from the layer. Requires
    /// <see cref="AuthorizationOperation.Query"/>.
    /// </summary>
    Read,

    /// <summary>
    /// The process adds features to the layer and never reads it. Requires
    /// <see cref="AuthorizationOperation.Insert"/>; a read grant is deliberately NOT required,
    /// so a caller may import into a layer whose contents they cannot query.
    /// </summary>
    Insert,

    /// <summary>
    /// The process modifies existing features in the layer. Requires
    /// <see cref="AuthorizationOperation.Update"/>.
    /// </summary>
    Update,

    /// <summary>
    /// The process removes features from the layer. Requires
    /// <see cref="AuthorizationOperation.Delete"/>.
    /// </summary>
    Delete,
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
