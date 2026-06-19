// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.ControlPlane;

/// <summary>
/// Default executable script executor for the additive layer-evolution path. The Metadata v2 graph
/// is the canonical source of truth for a resource's field set (<see cref="MetadataV2Resource.SchemaFields"/>),
/// so the additive forward change adds a nullable field to the target resource and the reversible
/// inverse drops it. Both directions are idempotent: re-adding an existing field or re-dropping an
/// absent field is a no-op. Storage-side DDL backfill, when needed, is dispatched separately through
/// the data-populate (ETL) job stage.
/// </summary>
internal sealed partial class MetadataReleaseScriptExecutor(
    IServiceScopeFactory scopeFactory,
    ILogger<MetadataReleaseScriptExecutor> logger) : IMetadataReleaseScriptExecutor
{
    public Task ApplyForwardAsync(MetadataReleaseExecutionPlan plan, CancellationToken cancellationToken = default)
        => ApplyOperationsAsync(plan, plan.Script.ForwardOperations, cancellationToken);

    public Task ApplyInverseAsync(MetadataReleaseExecutionPlan plan, CancellationToken cancellationToken = default)
        => ApplyOperationsAsync(plan, plan.Script.ResolveInverseOperations(), cancellationToken);

    private async Task ApplyOperationsAsync(
        MetadataReleaseExecutionPlan plan,
        IReadOnlyList<MetadataReleaseScriptOperation> operations,
        CancellationToken cancellationToken)
    {
        if (operations.Count == 0)
        {
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetService<IMetadataV2GraphStore>();
        if (store is null)
        {
            // Read-only graph backends cannot apply schema scripts; nothing to mutate.
            Log.NoWritableGraphStore(logger, plan.Script.ScriptId);
            return;
        }

        var current = await store.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        var graph = current.Graph;
        var resources = graph.Resources.ToList();
        var mutated = false;

        foreach (var operation in operations)
        {
            var index = resources.FindIndex(resource =>
                string.Equals(resource.Metadata.Id, operation.ResourceSemanticId, StringComparison.Ordinal));
            if (index < 0)
            {
                throw new InvalidOperationException(
                    $"Resource '{operation.ResourceSemanticId}' was not found in the current graph; cannot apply script operation.");
            }

            var resource = resources[index];
            var fields = resource.SchemaFields.ToList();
            var existing = fields.FindIndex(field =>
                string.Equals(field.Name, operation.FieldName, StringComparison.OrdinalIgnoreCase));

            switch (operation.Kind)
            {
                case MetadataReleaseScriptOperationKind.AddColumn when existing < 0:
                    fields.Add(new MetadataV2Field
                    {
                        Name = operation.FieldName,
                        Type = ParseFieldType(operation.FieldType),
                        Nullable = operation.Nullable
                    });
                    resources[index] = resource with { SchemaFields = fields };
                    mutated = true;
                    Log.AppliedAddColumn(logger, plan.Script.ScriptId, operation.ResourceSemanticId, operation.FieldName);
                    break;

                case MetadataReleaseScriptOperationKind.DropColumn when existing >= 0:
                    fields.RemoveAt(existing);
                    resources[index] = resource with { SchemaFields = fields };
                    mutated = true;
                    Log.AppliedDropColumn(logger, plan.Script.ScriptId, operation.ResourceSemanticId, operation.FieldName);
                    break;

                default:
                    // Idempotent no-op: add of an existing field or drop of an absent field.
                    break;
            }
        }

        if (!mutated)
        {
            return;
        }

        var nextGraph = graph with { Resources = resources };
        await store.SaveAsync(nextGraph, current.Etag, cancellationToken).ConfigureAwait(false);
    }

    private static MetadataV2FieldType ParseFieldType(string? raw)
        => Enum.TryParse<MetadataV2FieldType>(raw, ignoreCase: true, out var parsed)
            ? parsed
            : MetadataV2FieldType.String;

    private static partial class Log
    {
        [LoggerMessage(9130, LogLevel.Information, "Applied additive add-column for script {ScriptId} on resource {ResourceId} field {FieldName}")]
        public static partial void AppliedAddColumn(ILogger logger, string scriptId, string resourceId, string fieldName);

        [LoggerMessage(9131, LogLevel.Information, "Applied inverse drop-column for script {ScriptId} on resource {ResourceId} field {FieldName}")]
        public static partial void AppliedDropColumn(ILogger logger, string scriptId, string resourceId, string fieldName);

        [LoggerMessage(9132, LogLevel.Warning, "Metadata graph store is read-only; script {ScriptId} could not be applied")]
        public static partial void NoWritableGraphStore(ILogger logger, string scriptId);
    }
}
