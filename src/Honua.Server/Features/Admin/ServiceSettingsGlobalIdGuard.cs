// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Server.Features.Admin;

internal static partial class ServiceSettingsEndpoints
{
    private readonly record struct LayerMetadataTarget(string ServiceName, int ServiceLayerIndex, string ResourceId);

    private readonly record struct LayerMetadataMutation(
        IAdoNetDatabaseConnectionProvider Connections,
        LayerMetadataTarget Target,
        MetadataV2EditingPatch? EditingPatch);

    private readonly record struct EditingGraphUpdate(MetadataV2GraphSnapshot Snapshot, MetadataV2Graph Updated);

    private readonly record struct ManagedGlobalIdCheck(
        MetadataV2Graph Graph,
        MetadataV2Object Resource,
        string FieldName);

    private readonly record struct ManagedGlobalIdTarget(string QualifiedTable, int LayerId);

    // The test host stores the metadata graph in memory, so this runs before the write.
    // Production still builds the unique index; a refusal leaves the stored rows unchanged.
    private static async Task<(MetadataV2Graph Graph, MetadataV2Object Resource)> ApplyEditingPatchAsync(
        LayerMetadataMutation mutation,
        EditingGraphUpdate update,
        CancellationToken cancellationToken)
    {
        var snapshot = update.Snapshot;
        var updated = update.Updated;
        var editingPatch = mutation.EditingPatch ?? throw new ArgumentNullException(nameof(mutation));
        var serviceIds = snapshot.Graph.Services.Where(service =>
                string.Equals(service.Metadata.Name, mutation.Target.ServiceName, StringComparison.OrdinalIgnoreCase) &&
                ServiceProtocols.IsProtocolEnabled(service, ServiceProtocols.FeatureServer))
            .Select(service => service.Metadata.Id).ToHashSet(StringComparer.Ordinal);
        var publicationIds = snapshot.Graph.Publications.Where(publication =>
                publication.ResourceId == mutation.Target.ResourceId && serviceIds.Contains(publication.ServiceId) &&
                publication.PublicationType == MetadataV2LayerType.EsriFeatureLayer &&
                publication.LayerIndex == mutation.Target.ServiceLayerIndex && snapshot.IsRoutable(publication))
            .Select(publication => publication.Metadata.Id).ToHashSet(StringComparer.Ordinal);
        updated = MetadataV2EditingConfiguration.Apply(updated, mutation.Target.ResourceId, publicationIds, editingPatch);
        var resource = updated.Resources.Single(candidate => candidate.Metadata.Id == mutation.Target.ResourceId);
        if (editingPatch.GlobalIdField is { Length: > 0 } && resource.Editing?.GlobalIdField is { } globalIdField)
        {
            await EnsureManagedGlobalIdValuesAreDistinctAsync(
                mutation.Connections,
                new ManagedGlobalIdCheck(updated, resource, globalIdField),
                cancellationToken).ConfigureAwait(false);
        }

        return (updated, resource);
    }

    private static async Task EnsureManagedGlobalIdValuesAreDistinctAsync(
        IAdoNetDatabaseConnectionProvider connections,
        ManagedGlobalIdCheck check,
        CancellationToken cancellationToken)
    {
        if (TryGetManagedGlobalIdTarget(check) is not { } target)
        {
            return;
        }

        await using var connection = await connections.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT 1
            FROM {target.QualifiedTable}
            WHERE layer_id = @layer
              AND NULLIF(attributes->>@field, '') IS NOT NULL
            GROUP BY (NULLIF(attributes->>@field, '')::uuid)
            HAVING COUNT(*) > 1
            LIMIT 1
            """;
        AddParameter(command, "@layer", target.LayerId);
        AddParameter(command, "@field", check.FieldName);
        try
        {
            var duplicate = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (duplicate is not null and not DBNull)
            {
                throw new MetadataV2GlobalIdIntegrityException(
                    new InvalidOperationException($"Global-ID field '{check.FieldName}' has duplicate values."));
            }
        }
        catch (DbException ex) when (ex.SqlState == "22P02")
        {
            throw new MetadataV2GlobalIdIntegrityException(ex);
        }
    }

    private static ManagedGlobalIdTarget? TryGetManagedGlobalIdTarget(ManagedGlobalIdCheck check)
    {
        var binding = check.Graph.StorageBindings.FirstOrDefault(candidate =>
            candidate.ResourceId == check.Resource.Metadata.Id && candidate.StorageLayerId is not null);
        if (binding?.StorageLayerId is not int layerId)
        {
            return null;
        }

        FeatureStorageMapping mapping;
        try
        {
            mapping = FeatureStorageMapping.FromMetadata(check.Resource, binding);
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        if (!string.Equals(mapping.TableName, FeatureStorageMapping.ManagedFeaturesTableName, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(mapping.AttributesColumn, "attributes", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(mapping.LayerDiscriminatorColumn, "layer_id", StringComparison.OrdinalIgnoreCase)
            || !IsSqlIdentifier(check.FieldName)
            || !IsSqlIdentifier(mapping.TableName)
            || (mapping.SchemaName is { } schemaName && !IsSqlIdentifier(schemaName)))
        {
            return null;
        }

        var qualified = mapping.SchemaName is { Length: > 0 } schema
            ? $"{QuoteIdentifier(schema)}.{QuoteIdentifier(mapping.TableName)}"
            : QuoteIdentifier(mapping.TableName);
        return new ManagedGlobalIdTarget(qualified, layerId);
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static bool IsSqlIdentifier(string value)
        => value.Length is > 0 and <= 63
            && (value[0] is '_' or (>= 'A' and <= 'Z') or (>= 'a' and <= 'z'))
            && value.All(static character => character is '_' or (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9'));

    private static string QuoteIdentifier(string value) => "\"" + value + "\"";
}
