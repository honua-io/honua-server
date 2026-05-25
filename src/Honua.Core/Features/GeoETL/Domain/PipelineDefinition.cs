// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.GeoETL.Domain;

/// <summary>
/// The kind of a single stage in a pipeline. A pipeline is a linear chain of
/// <see cref="Source"/> followed by zero or more <see cref="Transform"/> stages
/// and terminated by a single <see cref="Sink"/>.
/// </summary>
public enum PipelineStageKind
{
    /// <summary>
    /// Extract stage that produces a feature stream from an external source.
    /// </summary>
    Source,

    /// <summary>
    /// Transform stage that maps an input feature stream to an output feature stream.
    /// </summary>
    Transform,

    /// <summary>
    /// Load stage that writes the feature stream to a durable target.
    /// </summary>
    Sink
}

/// <summary>
/// Worker runtime profile required to execute a connector. The serving image runs
/// only <see cref="Managed"/> connectors; <see cref="RequiresNativeGeo"/> connectors
/// run on the dedicated <c>honua-worker-etl</c> image (Child Ticket F). See ADR-0038.
/// </summary>
public enum ConnectorRuntimeProfile
{
    /// <summary>
    /// Managed connector with no native GDAL/PROJ/GEOS dependency. Runs in the lean
    /// serving image.
    /// </summary>
    Managed,

    /// <summary>
    /// Connector that requires native geospatial libraries (GDAL/OGR). Runs only on
    /// the heavyweight worker profile.
    /// </summary>
    RequiresNativeGeo
}

/// <summary>
/// Typed configuration for a source or sink connector stage. Carries the connector
/// type discriminator plus an opaque, source-generation-friendly string option bag.
/// </summary>
/// <remarks>
/// The option bag is intentionally <see cref="string"/>-keyed and -valued so that the
/// definition remains trim/AOT safe and source-generated polymorphic JSON stays flat.
/// Connector factories interpret <see cref="Options"/> per connector type.
/// </remarks>
public sealed record ConnectorConfig
{
    /// <summary>
    /// Stable connector type discriminator, for example <c>geojson</c>, <c>shapefile</c>,
    /// or <c>honua-layer</c>. Resolved by the connector factory.
    /// </summary>
    public required string Type { get; init; }

    /// <summary>
    /// Opaque connector-specific options interpreted by the connector factory.
    /// </summary>
    public IReadOnlyDictionary<string, string> Options { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>
/// Typed configuration for a transform stage. Carries the transform type discriminator
/// plus an opaque, source-generation-friendly string option bag.
/// </summary>
public sealed record TransformConfig
{
    /// <summary>
    /// Stable transform type discriminator, for example <c>reproject</c> or
    /// <c>attribute-filter</c>. Resolved by the transform factory.
    /// </summary>
    public required string Type { get; init; }

    /// <summary>
    /// Opaque transform-specific options interpreted by the transform factory.
    /// </summary>
    public IReadOnlyDictionary<string, string> Options { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>
/// A single stage in a pipeline. Exactly one of <see cref="Connector"/> or
/// <see cref="Transform"/> is populated depending on <see cref="Kind"/>.
/// </summary>
public sealed record PipelineStage
{
    /// <summary>
    /// The stage kind. Determines which of <see cref="Connector"/> or
    /// <see cref="Transform"/> carries this stage's configuration.
    /// </summary>
    public required PipelineStageKind Kind { get; init; }

    /// <summary>
    /// Connector configuration for <see cref="PipelineStageKind.Source"/> and
    /// <see cref="PipelineStageKind.Sink"/> stages. Null for transform stages.
    /// </summary>
    public ConnectorConfig? Connector { get; init; }

    /// <summary>
    /// Transform configuration for <see cref="PipelineStageKind.Transform"/> stages.
    /// Null for source and sink stages.
    /// </summary>
    public TransformConfig? Transform { get; init; }
}

/// <summary>
/// A declarative GeoETL pipeline: a single source, zero or more transforms, and a
/// single sink. Stored in <c>honua.pipeline_definitions</c> and executed as an
/// <see cref="Honua.Core.Features.ControlPlane.Domain.ExecutionJobKind.ExtractTransformLoad"/>
/// job through the durable substrate (#681). See the GeoETL roadmap and ADR-0038.
/// </summary>
public sealed record PipelineDefinition
{
    /// <summary>
    /// Schema version of the definition document. Lives on the root from day one so
    /// that stored definitions remain readable across discriminated-union evolutions.
    /// </summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>
    /// Stable pipeline identifier. Assigned at creation time.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Human-readable pipeline name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Optional free-form description.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Definition version. Incremented on every successful update. Older versions
    /// remain readable for audit and rollback.
    /// </summary>
    public int Version { get; init; } = 1;

    /// <summary>
    /// Ordered stage chain: Source then []Transform then Sink.
    /// </summary>
    public IReadOnlyList<PipelineStage> Stages { get; init; } = [];

    /// <summary>
    /// UTC creation timestamp.
    /// </summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// UTC timestamp of the most recent update.
    /// </summary>
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>
    /// Returns the single source stage, or null when the chain is malformed.
    /// </summary>
    public PipelineStage? Source =>
        Stages.Count > 0 && Stages[0].Kind == PipelineStageKind.Source ? Stages[0] : null;

    /// <summary>
    /// Returns the single sink stage, or null when the chain is malformed.
    /// </summary>
    public PipelineStage? Sink =>
        Stages.Count > 0 && Stages[^1].Kind == PipelineStageKind.Sink ? Stages[^1] : null;

    /// <summary>
    /// Returns the ordered transform stages between the source and sink.
    /// </summary>
    public IReadOnlyList<PipelineStage> Transforms =>
        Stages.Where(s => s.Kind == PipelineStageKind.Transform).ToArray();
}
