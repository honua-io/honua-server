// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.WorkflowPackages.Domain;

namespace Honua.Server.Features.Operations.ServicePublish;

/// <summary>
/// Operation provider that surfaces the <c>service.publish</c> operation into the
/// catalog, with its execution lane, approval lane, and policy metadata. Mirrors the
/// workflow node provider pattern one level up.
/// </summary>
internal sealed class ServicePublishOperationProvider : IOperationProvider
{
    /// <summary>
    /// Stable provider identifier.
    /// </summary>
    public const string Id = "honua.studio";

    public string ProviderId => Id;

    public Task<IReadOnlyList<HonuaOperationDefinition>> ListOperationsAsync(
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<HonuaOperationDefinition> operations =
        [
            new HonuaOperationDefinition
            {
                OperationId = ServicePublishOperation.Id,
                ProviderId = Id,
                Title = "Publish service layer",
                Description =
                    "Publishes a PostGIS table as a Honua layer within a FeatureServer service.",
                Category = "Studio",
                ParameterSchemas = BuildParameterSchemas(),
                OutputSchema = BuildOutputSchema(),
                // Layer publish is a direct, inline action today.
                ExecutionLane = OperationExecutionLane.Synchronous,
                ApprovalLane = OperationApprovalLane.None,
                Policy = new OperationPolicyMetadata
                {
                    BlastRadiusClass = OperationBlastRadiusClass.ServiceScope,
                    SideEffectClass = OperationSideEffectClass.CreatesMetadata,
                    Determinism = OperationDeterminism.Deterministic,
                    // Validation exists via ILayerPublishingService.ValidateTableForPublishAsync.
                    SupportsDryRun = true
                }
            }
        ];

        return Task.FromResult(operations);
    }

    private static IReadOnlyList<WorkflowNodeParameterSchema> BuildParameterSchemas() =>
    [
        TextParameter("connectionId", "Connection", "Secure connection identifier or name to publish against.", required: true),
        TextParameter("schema", "Schema", "Schema containing the source table.", required: true),
        TextParameter("table", "Table", "Source table name.", required: true),
        TextParameter("layerName", "Layer name", "Display name for the published layer.", required: true),
        TextParameter("description", "Description", "Optional layer description.", required: false),
        TextParameter("geometryColumn", "Geometry column", "Geometry column name.", required: false),
        TextParameter("geometryType", "Geometry type", "Geometry type (Point, Polygon, etc).", required: false),
        new WorkflowNodeParameterSchema
        {
            Name = "srid",
            DisplayName = "SRID",
            Description = "Spatial reference identifier.",
            Required = false,
            Schema = new WorkflowSchemaDefinition { Type = WorkflowSchemaValueType.WholeNumber, Format = "srid" }
        },
        TextParameter("primaryKey", "Primary key", "Primary key field name.", required: false),
        new WorkflowNodeParameterSchema
        {
            Name = "fields",
            DisplayName = "Fields",
            Description = "Attribute fields to publish (empty means include all).",
            Required = false,
            Schema = new WorkflowSchemaDefinition
            {
                Type = WorkflowSchemaValueType.List,
                Items = new WorkflowSchemaDefinition { Type = WorkflowSchemaValueType.Text }
            }
        },
        TextParameter("serviceName", "Service name", "Target service name (defaults to \"default\").", required: false),
        new WorkflowNodeParameterSchema
        {
            Name = "enabled",
            DisplayName = "Enabled",
            Description = "Whether the layer is enabled after publishing.",
            Required = false,
            DefaultValue = "true",
            Schema = new WorkflowSchemaDefinition { Type = WorkflowSchemaValueType.Flag }
        }
    ];

    private static WorkflowSchemaDefinition BuildOutputSchema() => new()
    {
        Type = WorkflowSchemaValueType.Structured,
        Format = "published-layer-summary",
        Properties = new Dictionary<string, WorkflowSchemaDefinition>(StringComparer.Ordinal)
        {
            ["layerId"] = new() { Type = WorkflowSchemaValueType.WholeNumber },
            ["layerName"] = new() { Type = WorkflowSchemaValueType.Text },
            ["serviceName"] = new() { Type = WorkflowSchemaValueType.Text },
            ["schema"] = new() { Type = WorkflowSchemaValueType.Text },
            ["table"] = new() { Type = WorkflowSchemaValueType.Text },
            ["geometryType"] = new() { Type = WorkflowSchemaValueType.Text },
            ["srid"] = new() { Type = WorkflowSchemaValueType.WholeNumber, Format = "srid" },
            ["fieldCount"] = new() { Type = WorkflowSchemaValueType.WholeNumber },
            ["enabled"] = new() { Type = WorkflowSchemaValueType.Flag }
        }
    };

    private static WorkflowNodeParameterSchema TextParameter(
        string name,
        string displayName,
        string description,
        bool required) => new()
        {
            Name = name,
            DisplayName = displayName,
            Description = description,
            Required = required,
            Schema = new WorkflowSchemaDefinition { Type = WorkflowSchemaValueType.Text }
        };
}
