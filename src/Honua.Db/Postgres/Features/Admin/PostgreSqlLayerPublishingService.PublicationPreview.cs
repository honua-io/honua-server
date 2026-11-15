// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Admin.Domain;
using Honua.Db.Postgres.Features.FileImport;
using Honua.Db.Postgres.Features.Infrastructure;
using Npgsql;

namespace Honua.Db.Postgres.Features.Admin;

internal sealed partial class PostgreSqlLayerPublishingService : ILayerPublicationPreviewReader
{
    public async Task<LayerPublicationPreview> PreviewPublicationAsync(
        string connectionString, LayerPublishRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(request);
        using var pinnedSchema = OpenCommittedSchemaScope(connectionString,
            request.ConnectionEndpointFingerprint, request.CommittedSinkPublication, request.Schema, request.Table);
        ValidatePublicationRequest(request);
        // This operation previews the source-backed service.publish contract. Other publication
        // modes need their own preview contracts before they can be admitted here.
        if (request.StorageMode == LayerStorageMode.Managed || request.CreateEditableCopy)
        {
            throw new LayerPublishingException(LayerPublishingErrorKind.Validation,
                "Publication preview requires source-backed storage.");
        }

        var schema = request.Schema.Trim();
        var table = request.Table.Trim();
        var serviceName = NormalizeServiceName(request.ServiceName);
        var scope = ResolvePublicationScope(request.Namespace, _tenantContext?.TenantId);
        var (graph, etag) = await LoadCurrentOrEmptyGraphAsync(cancellationToken).ConfigureAwait(false);
        if (etag is null)
        {
            var bootstrap = await _metadataGraphStore.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
            graph = bootstrap.Graph;
            etag = bootstrap.Etag;
        }
        ValidatePublicationScope(graph, serviceName, scope);
        var capabilities = ResolvePublicationCapabilities(request.Capabilities, managedStore: false);
        await using var connection = new NpgsqlConnection(
            (sessionLockConnections?.Value ?? PostgresSessionLockConnections.Pooled).Resolve(connectionString));
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var importSourceLock = await ImportedDatasetSpatialMetadata.AcquireSessionLockAsync(
            connection, schema, table, cancellationToken).ConfigureAwait(false);
        var source = await PreparePublicationSourceAsync(connection, new PublicationSourceRequest
        {
            ConnectionString = connectionString,
            Request = request,
            ServiceName = serviceName
        }, cancellationToken).ConfigureAwait(false);

        await using var transaction = await connection.BeginTransactionAsync(
            System.Data.IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false);
        // The remaining helpers may read metadata and take read locks; no SQL writes are allowed.
        await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction))
        {
            await readOnly.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await ImportedDatasetSpatialMetadata.LockRelationAsync(
            connection, transaction, schema, table, cancellationToken).ConfigureAwait(false);
        var protectedTable = await ResolveTableInfoForValidationAsync(connection, schema, table, cancellationToken, transaction);
        if (protectedTable is null || protectedTable.GeometryColumn != source.Table.GeometryColumn ||
            protectedTable.GeometryType != source.Table.GeometryType || protectedTable.Srid != source.Table.Srid ||
            protectedTable.Columns.Count != source.Table.Columns.Count ||
            !protectedTable.Columns.Zip(source.Table.Columns).All(pair => pair.First.Name == pair.Second.Name &&
                pair.First.DataType == pair.Second.DataType && pair.First.IsNullable == pair.Second.IsNullable &&
                pair.First.IsPrimaryKey == pair.Second.IsPrimaryKey && pair.First.MaxLength == pair.Second.MaxLength))
        {
            throw new LayerPublishingException(LayerPublishingErrorKind.Validation,
                "Source schema changed while preparing publication; retry against the current source.");
        }
        if (await FindExistingLayerAsync(connection, transaction, schema, table, cancellationToken).ConfigureAwait(false) is not null)
        {
            throw new LayerPublishingException(LayerPublishingErrorKind.Conflict,
                $"Layer already exists for table '{schema}.{table}'.");
        }
        var storage = PublishedLayerStorage.ForSourceTable(schema, table, source.PrimaryKeyColumn!.Name,
            source.GeometryColumn, source.StorageSrid,
            source.SelectedColumns.Where(column => column.DataType.Equals("smallint", StringComparison.OrdinalIgnoreCase))
                .Select(column => column.Name).ToArray());
        var epoch = await ResolveImportedStorageEpochAsync(connection, transaction, request,
            schema, table, source.GeometryColumn, source.StorageSrid, storage.StorageSrid,
            cancellationToken).ConfigureAwait(false);
        var extent = await ReadLayerExtentAsync(connection, transaction, schema, table, source.GeometryColumn,
            source.StorageSrid, managedLayerId: null, cancellationToken).ConfigureAwait(false);

        // This ID exists only in the in-memory plan. Never advance the SQL sequence or
        // advertise the resulting route as allocated. Avoid replacing an existing graph node.
        var placeholder = int.MaxValue;
        while (graph.Resources.Any(resource => resource.Metadata.Id == BuildResourceId(placeholder, request.ConnectionId)) ||
               graph.StorageBindings.Any(binding => binding.Metadata.Id == BuildStorageBindingId(placeholder, request.ConnectionId)))
        {
            placeholder--;
        }
        var planned = BuildPublishedLayerGraph(new PublishedLayerGraphPlan
        {
            Graph = graph,
            Request = request,
            ServiceName = serviceName,
            LayerId = placeholder,
            Storage = storage,
            PrimaryKey = source.PrimaryKeyColumn.Name,
            GeometryColumn = source.GeometryColumn,
            GeometryType = source.GeometryType,
            Srid = source.Srid,
            StorageCoordinateEpoch = epoch,
            Fields = source.Fields,
            Extent = extent,
            Capabilities = capabilities,
            Scope = scope,
            GeneratedAt = DateTimeOffset.UtcNow
        });
        var resourceId = BuildResourceId(placeholder, request.ConnectionId);
        var bindingId = BuildStorageBindingId(placeholder, request.ConnectionId);
        var resource = planned.Resources.Single(candidate => candidate.Metadata.Id == resourceId);
        var binding = planned.StorageBindings.Single(candidate => candidate.Metadata.Id == bindingId);
        var publications = planned.Publications.Where(candidate => candidate.ResourceId == resourceId).ToArray();
        var service = planned.Services.Single(candidate => candidate.Metadata.Id == publications[0].ServiceId);
        // Return only this plan's objects, never unrelated graph objects or connection secrets.
        return new LayerPublicationPreview(new LayerPublicationBase(graph.Revision, etag),
            service, resource, binding, publications);
    }
}
