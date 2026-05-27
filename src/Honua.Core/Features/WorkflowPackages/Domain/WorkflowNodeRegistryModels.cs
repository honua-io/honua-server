// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Core.Features.WorkflowPackages.Domain;

/// <summary>
/// Versioned snapshot of every workflow node definition currently available to Console clients.
/// </summary>
public sealed record WorkflowNodeRegistrySnapshot
{
    /// <summary>
    /// Stable version fingerprint for this registry snapshot.
    /// </summary>
    public required string RegistryVersion { get; init; }

    /// <summary>
    /// UTC timestamp when the snapshot was generated.
    /// </summary>
    public required DateTimeOffset GeneratedAt { get; init; }

    /// <summary>
    /// Provider snapshots that contributed node definitions or warnings.
    /// </summary>
    public required IReadOnlyList<WorkflowNodeProviderSnapshot> Providers { get; init; }

    /// <summary>
    /// Node definitions available for workflow graph authoring.
    /// </summary>
    public required IReadOnlyList<WorkflowNodeDefinition> Nodes { get; init; }

    /// <summary>
    /// Non-fatal registry-level warnings.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
/// Describes a single node provider that contributed to a registry snapshot.
/// </summary>
public sealed record WorkflowNodeProviderSnapshot
{
    /// <summary>
    /// Stable provider identifier.
    /// </summary>
    public required string ProviderId { get; init; }

    /// <summary>
    /// Provider implementation or catalog version.
    /// </summary>
    public required string Version { get; init; }

    /// <summary>
    /// Whether the provider produced executable node definitions.
    /// </summary>
    public bool Available { get; init; } = true;

    /// <summary>
    /// Provider warnings that should be rendered by authoring clients.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
/// Server-owned description of a node type that may appear in a workflow graph.
/// </summary>
public sealed record WorkflowNodeDefinition
{
    /// <summary>
    /// Stable node type identifier, for example <c>process:geometry.buffer</c>.
    /// </summary>
    public required string NodeTypeId { get; init; }

    /// <summary>
    /// Provider that owns the node type.
    /// </summary>
    public required string ProviderId { get; init; }

    /// <summary>
    /// Runtime family used to execute this node.
    /// </summary>
    public required WorkflowNodeRuntimeKind RuntimeKind { get; init; }

    /// <summary>
    /// Human-readable node title.
    /// </summary>
    public required string Title { get; init; }

    /// <summary>
    /// Short node description.
    /// </summary>
    public required string Description { get; init; }

    /// <summary>
    /// Palette category for grouping related nodes.
    /// </summary>
    public required string Category { get; init; }

    /// <summary>
    /// Parameter schemas used by Console forms and server validation.
    /// </summary>
    public required IReadOnlyList<WorkflowNodeParameterSchema> ParameterSchemas { get; init; }

    /// <summary>
    /// Data input schemas used by graph edge compatibility checks.
    /// </summary>
    public IReadOnlyList<WorkflowNodePortSchema> InputSchemas { get; init; } = [];

    /// <summary>
    /// Data output schemas produced by the node.
    /// </summary>
    public IReadOnlyList<WorkflowNodePortSchema> OutputSchemas { get; init; } = [];

    /// <summary>
    /// Capabilities exposed by this node type.
    /// </summary>
    public required WorkflowNodeCapabilityFlags CapabilityFlags { get; init; }

    /// <summary>
    /// Runtime and cost hints for scheduling and UI display.
    /// </summary>
    public required WorkflowNodeRuntimeHints RuntimeHints { get; init; }

    /// <summary>
    /// Non-fatal warnings about missing provider capability, license, or target eligibility.
    /// </summary>
    public IReadOnlyList<string> EligibilityWarnings { get; init; } = [];

    /// <summary>
    /// Optional canonical process identifier when this node adapts a geoprocessing process.
    /// </summary>
    public string? ProcessId { get; init; }
}

/// <summary>
/// Schema for a configurable node parameter.
/// </summary>
public sealed record WorkflowNodeParameterSchema
{
    /// <summary>
    /// Machine-readable parameter name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Display label.
    /// </summary>
    public required string DisplayName { get; init; }

    /// <summary>
    /// Parameter description.
    /// </summary>
    public required string Description { get; init; }

    /// <summary>
    /// Whether the parameter must be supplied.
    /// </summary>
    public bool Required { get; init; }

    /// <summary>
    /// Default value represented as a string.
    /// </summary>
    public string? DefaultValue { get; init; }

    /// <summary>
    /// Constrained schema for the parameter value.
    /// </summary>
    public required WorkflowSchemaDefinition Schema { get; init; }
}

/// <summary>
/// Schema for a workflow node input or output port.
/// </summary>
public sealed record WorkflowNodePortSchema
{
    /// <summary>
    /// Port name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Human-readable port title.
    /// </summary>
    public required string Title { get; init; }

    /// <summary>
    /// Whether the port must be connected or supplied.
    /// </summary>
    public bool Required { get; init; }

    /// <summary>
    /// Port value schema.
    /// </summary>
    public required WorkflowSchemaDefinition Schema { get; init; }
}

/// <summary>
/// AOT-safe constrained schema subset used for workflow package contracts.
/// </summary>
public sealed record WorkflowSchemaDefinition
{
    /// <summary>
    /// JSON value type.
    /// </summary>
    public required WorkflowSchemaValueType Type { get; init; }

    /// <summary>
    /// Optional format hint such as <c>wkb</c>, <c>srid</c>, or <c>feature-layer</c>.
    /// </summary>
    public string? Format { get; init; }

    /// <summary>
    /// Optional unit hint such as <c>meters</c> or <c>square-meters</c>.
    /// </summary>
    public string? Unit { get; init; }

    /// <summary>
    /// Optional CRS/SRID hint for spatial values.
    /// </summary>
    public string? Crs { get; init; }

    /// <summary>
    /// Allowed string values when the value is constrained to an enumeration.
    /// </summary>
    public IReadOnlyList<string> EnumValues { get; init; } = [];

    /// <summary>
    /// Item schema for array values.
    /// </summary>
    public WorkflowSchemaDefinition? Items { get; init; }

    /// <summary>
    /// Object property schemas keyed by property name.
    /// </summary>
    public IReadOnlyDictionary<string, WorkflowSchemaDefinition> Properties { get; init; } =
        new Dictionary<string, WorkflowSchemaDefinition>();
}

/// <summary>
/// Capability flags advertised for a workflow node type.
/// </summary>
public sealed record WorkflowNodeCapabilityFlags
{
    /// <summary>
    /// Whether the node can be structurally validated.
    /// </summary>
    public bool CanValidate { get; init; }

    /// <summary>
    /// Whether the node can participate in bounded dry runs.
    /// </summary>
    public bool CanDryRun { get; init; }

    /// <summary>
    /// Whether the node can run through the durable job substrate.
    /// </summary>
    public bool SupportsJob { get; init; }

    /// <summary>
    /// Whether the node can be compiled into a scheduled workflow definition.
    /// </summary>
    public bool SupportsSchedule { get; init; }

    /// <summary>
    /// Whether the node can be exposed through a process-style endpoint.
    /// </summary>
    public bool SupportsProcessEndpoint { get; init; }

    /// <summary>
    /// Whether the node is currently executable on this server.
    /// </summary>
    public bool Executable { get; init; }
}

/// <summary>
/// Runtime and cost hints for a node definition.
/// </summary>
public sealed record WorkflowNodeRuntimeHints
{
    /// <summary>
    /// Worker profile expected to execute the node.
    /// </summary>
    public string? WorkerProfile { get; init; }

    /// <summary>
    /// Low-cost duration estimate in seconds.
    /// </summary>
    public int? EstimatedDurationSeconds { get; init; }

    /// <summary>
    /// Relative cost weight used by admission and UI hints.
    /// </summary>
    public double? CostWeight { get; init; }

    /// <summary>
    /// Human-readable cost unit.
    /// </summary>
    public string? CostUnit { get; init; }
}

/// <summary>
/// Converts process catalog value types to workflow schema definitions.
/// </summary>
public static class WorkflowSchemaMappings
{
    /// <summary>
    /// Maps a process parameter type into the workflow schema subset.
    /// </summary>
    public static WorkflowSchemaDefinition FromProcessParameterType(ProcessParameterValueType valueType)
        => valueType switch
        {
            ProcessParameterValueType.Text => new WorkflowSchemaDefinition { Type = WorkflowSchemaValueType.Text },
            ProcessParameterValueType.WholeNumber => new WorkflowSchemaDefinition { Type = WorkflowSchemaValueType.WholeNumber },
            ProcessParameterValueType.FloatingPoint => new WorkflowSchemaDefinition { Type = WorkflowSchemaValueType.DecimalNumber },
            ProcessParameterValueType.Flag => new WorkflowSchemaDefinition { Type = WorkflowSchemaValueType.Flag },
            ProcessParameterValueType.Wkb => new WorkflowSchemaDefinition
            {
                Type = WorkflowSchemaValueType.Text,
                Format = "wkb",
                Crs = "parameter:srid"
            },
            ProcessParameterValueType.WkbArray => new WorkflowSchemaDefinition
            {
                Type = WorkflowSchemaValueType.List,
                Items = new WorkflowSchemaDefinition
                {
                    Type = WorkflowSchemaValueType.Text,
                    Format = "wkb",
                    Crs = "parameter:srid"
                }
            },
            ProcessParameterValueType.Srid => new WorkflowSchemaDefinition
            {
                Type = WorkflowSchemaValueType.WholeNumber,
                Format = "srid"
            },
            ProcessParameterValueType.LayerId => new WorkflowSchemaDefinition
            {
                Type = WorkflowSchemaValueType.WholeNumber,
                Format = "layer-id"
            },
            _ => new WorkflowSchemaDefinition { Type = WorkflowSchemaValueType.Text }
        };

    /// <summary>
    /// Maps a geoprocessing artifact kind into a workflow output schema.
    /// </summary>
    public static WorkflowSchemaDefinition FromArtifactKind(ArtifactKind kind)
        => kind switch
        {
            ArtifactKind.FeatureLayer => new WorkflowSchemaDefinition
            {
                Type = WorkflowSchemaValueType.Structured,
                Format = "feature-layer"
            },
            ArtifactKind.Table => new WorkflowSchemaDefinition
            {
                Type = WorkflowSchemaValueType.Structured,
                Format = "table"
            },
            ArtifactKind.Raster => new WorkflowSchemaDefinition
            {
                Type = WorkflowSchemaValueType.Structured,
                Format = "raster"
            },
            ArtifactKind.Scalar => new WorkflowSchemaDefinition
            {
                Type = WorkflowSchemaValueType.DecimalNumber,
                Format = "scalar"
            },
            _ => new WorkflowSchemaDefinition
            {
                Type = WorkflowSchemaValueType.Structured,
                Format = kind.ToString()
            }
        };
}
