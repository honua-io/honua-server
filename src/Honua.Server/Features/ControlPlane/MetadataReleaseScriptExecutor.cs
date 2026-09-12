// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.ControlPlane;

/// <summary>
/// Default script executor for the additive layer-evolution path. The Metadata v2 graph is the
/// canonical source of truth for a resource's field set (<see cref="MetadataV2Resource.SchemaFields"/>),
/// so the forward change adds a nullable field to the target resource and the inverse drops exactly
/// the fields the release owns. Both directions are pure graph transforms: the reconciler stages the
/// result as an immutable candidate (forward) or commits it conditionally (inverse), so preparing a
/// change never touches the active revision. Storage-side backfill, when needed, is dispatched
/// separately through the data-populate (ETL) job stage.
/// </summary>
internal sealed partial class MetadataReleaseScriptExecutor(
    ILogger<MetadataReleaseScriptExecutor> logger) : IMetadataReleaseScriptExecutor
{
    internal const string ResourceMissing = "metadata-release-resource-missing";
    internal const string FieldConflict = "metadata-release-field-conflict";
    internal const string OwnedFieldModified = "metadata-release-owned-field-modified";

    public MetadataReleaseScriptResult PrepareForward(MetadataReleaseExecutionPlan plan, MetadataV2Graph baseline)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(baseline);

        var resources = baseline.Resources.ToList();
        var applied = new List<MetadataReleaseScriptOperation>();
        foreach (var operation in plan.Script.ForwardOperations)
        {
            if (operation.Kind != MetadataReleaseScriptOperationKind.AddColumn)
            {
                throw new MetadataReleasePreparationException(
                    MetadataReleaseChangePolicy.DestructiveChange,
                    $"Forward operation {operation.Kind} on '{operation.ResourceSemanticId}.{operation.FieldName}' is not additive.");
            }

            var index = FindResource(resources, operation.ResourceSemanticId);
            if (index < 0)
            {
                throw new MetadataReleasePreparationException(
                    ResourceMissing,
                    $"Resource '{operation.ResourceSemanticId}' was not found in the prior revision; the candidate cannot be prepared.");
            }

            var resource = resources[index];
            var fieldType = ParseFieldType(operation.FieldType);
            var existing = FindField(resource, operation.FieldName);
            if (existing is not null)
            {
                // Identical field already present: idempotent no-op that the release does not own, so
                // a rollback will never drop it. A conflicting definition is rejected, not overwritten.
                if (existing.Type != fieldType || existing.Nullable != operation.Nullable)
                {
                    throw new MetadataReleasePreparationException(
                        FieldConflict,
                        $"Field '{operation.ResourceSemanticId}.{existing.Name}' already exists as {existing.Type} (nullable: {existing.Nullable}); " +
                        $"the release declares {fieldType} (nullable: {operation.Nullable}).");
                }

                continue;
            }

            resources[index] = resource with
            {
                SchemaFields =
                [
                    .. resource.SchemaFields,
                    new MetadataV2Field
                    {
                        Name = operation.FieldName,
                        Type = fieldType,
                        Nullable = operation.Nullable
                    }
                ]
            };
            applied.Add(operation);
            Log.PreparedAddColumn(logger, plan.Script.ScriptId, operation.ResourceSemanticId, operation.FieldName);
        }

        return new MetadataReleaseScriptResult
        {
            Graph = baseline with { Resources = resources },
            AppliedOperations = applied
        };
    }

    public MetadataReleaseScriptResult PrepareInverse(
        IReadOnlyList<MetadataReleaseScriptOperation> ownedOperations,
        MetadataV2Graph current)
    {
        ArgumentNullException.ThrowIfNull(ownedOperations);
        ArgumentNullException.ThrowIfNull(current);

        var resources = current.Resources.ToList();
        var applied = new List<MetadataReleaseScriptOperation>();
        for (var i = ownedOperations.Count - 1; i >= 0; i--)
        {
            var owned = ownedOperations[i];
            var index = FindResource(resources, owned.ResourceSemanticId);
            var field = index < 0 ? null : FindField(resources[index], owned.FieldName);
            if (field is null)
            {
                // Already reverted (or the resource was removed by a later update): nothing of this
                // release remains to revert.
                continue;
            }

            if (field.Type != ParseFieldType(owned.FieldType) || field.Nullable != owned.Nullable)
            {
                throw new MetadataReleasePreparationException(
                    OwnedFieldModified,
                    $"Field '{owned.ResourceSemanticId}.{field.Name}' was changed after activation; reverting it would discard an unrelated update.");
            }

            var resource = resources[index];
            resources[index] = resource with
            {
                SchemaFields = resource.SchemaFields.Where(candidate => !ReferenceEquals(candidate, field)).ToArray()
            };
            applied.Add(new MetadataReleaseScriptOperation
            {
                Kind = MetadataReleaseScriptOperationKind.DropColumn,
                ResourceSemanticId = owned.ResourceSemanticId,
                FieldName = field.Name
            });
            Log.PreparedDropColumn(logger, owned.ResourceSemanticId, field.Name);
        }

        return new MetadataReleaseScriptResult
        {
            Graph = current with { Resources = resources },
            AppliedOperations = applied
        };
    }

    private static int FindResource(List<MetadataV2Resource> resources, string resourceSemanticId)
        => resources.FindIndex(resource => string.Equals(resource.Metadata.Id, resourceSemanticId, StringComparison.Ordinal));

    private static MetadataV2Field? FindField(MetadataV2Resource resource, string fieldName)
        => resource.SchemaFields.FirstOrDefault(field => string.Equals(field.Name, fieldName, StringComparison.OrdinalIgnoreCase));

    private static MetadataV2FieldType ParseFieldType(string? raw)
        => Enum.TryParse<MetadataV2FieldType>(raw, ignoreCase: true, out var parsed)
            ? parsed
            : MetadataV2FieldType.String;

    private static partial class Log
    {
        [LoggerMessage(9130, LogLevel.Information, "Prepared additive add-column for script {ScriptId} on resource {ResourceId} field {FieldName}")]
        public static partial void PreparedAddColumn(ILogger logger, string scriptId, string resourceId, string fieldName);

        [LoggerMessage(9131, LogLevel.Information, "Prepared inverse drop-column on resource {ResourceId} field {FieldName}")]
        public static partial void PreparedDropColumn(ILogger logger, string resourceId, string fieldName);
    }
}
