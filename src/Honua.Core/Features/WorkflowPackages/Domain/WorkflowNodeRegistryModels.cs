// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Core.Features.WorkflowPackages.Domain;

public sealed record WorkflowNodeRegistrySnapshot
{
    public required string RegistryVersion { get; init; }

    public required DateTimeOffset GeneratedAt { get; init; }

    public required IReadOnlyList<WorkflowNodeProviderSnapshot> Providers { get; init; }

    public required IReadOnlyList<WorkflowNodeDefinition> Nodes { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed record WorkflowNodeProviderSnapshot
{
    public required string ProviderId { get; init; }

    public required string Version { get; init; }

    public bool Available { get; init; } = true;

    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed record WorkflowNodeDefinition
{
    public required string NodeTypeId { get; init; }

    public required string ProviderId { get; init; }

    public required WorkflowNodeRuntimeKind RuntimeKind { get; init; }

    public required string Title { get; init; }

    public required string Description { get; init; }

    public required string Category { get; init; }

    public required IReadOnlyList<WorkflowNodeParameterSchema> ParameterSchemas { get; init; }

    public IReadOnlyList<WorkflowNodePortSchema> InputSchemas { get; init; } = [];

    public IReadOnlyList<WorkflowNodePortSchema> OutputSchemas { get; init; } = [];

    public required WorkflowNodeCapabilityFlags CapabilityFlags { get; init; }

    public required WorkflowNodeRuntimeHints RuntimeHints { get; init; }

    public IReadOnlyList<string> EligibilityWarnings { get; init; } = [];

    public string? ProcessId { get; init; }
}

public sealed record WorkflowNodeParameterSchema
{
    public required string Name { get; init; }

    public required string DisplayName { get; init; }

    public required string Description { get; init; }

    public bool Required { get; init; }

    public string? DefaultValue { get; init; }

    public required WorkflowSchemaDefinition Schema { get; init; }
}

public sealed record WorkflowNodePortSchema
{
    public required string Name { get; init; }

    public required string Title { get; init; }

    public bool Required { get; init; }

    public required WorkflowSchemaDefinition Schema { get; init; }
}

public sealed record WorkflowSchemaDefinition
{
    public required WorkflowSchemaValueType Type { get; init; }

    public string? Format { get; init; }

    public string? Unit { get; init; }

    public string? Crs { get; init; }

    public IReadOnlyList<string> EnumValues { get; init; } = [];

    public WorkflowSchemaDefinition? Items { get; init; }

    public IReadOnlyDictionary<string, WorkflowSchemaDefinition> Properties { get; init; } =
        new Dictionary<string, WorkflowSchemaDefinition>();

    public IReadOnlyList<string> RequiredProperties { get; init; } = [];
}

public sealed record WorkflowNodeCapabilityFlags
{
    public bool CanValidate { get; init; }

    public bool CanDryRun { get; init; }

    public bool SupportsJob { get; init; }

    public bool SupportsSchedule { get; init; }

    public bool SupportsProcessEndpoint { get; init; }

    public bool Executable { get; init; }
}

public sealed record WorkflowNodeRuntimeHints
{
    public string? WorkerProfile { get; init; }

    public int? EstimatedDurationSeconds { get; init; }

    public double? CostWeight { get; init; }

    public string? CostUnit { get; init; }
}

public static class WorkflowSchemaMappings
{
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
