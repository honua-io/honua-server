// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Db.Postgres.Features.FileImport;
using Honua.Db.Postgres.Features.Infrastructure;
using Npgsql;

namespace Honua.Db.Postgres.Features.Admin;

internal sealed partial class PostgreSqlLayerPublishingService : ILayerPublicationPreviewReader
{
    private sealed record PublicationPreviewContext
    {
        public required LayerPublishRequest Request { get; init; }
        public required PreparedPublicationSource Source { get; init; }
        public required MetadataV2Graph Graph { get; init; }
        public required LayerPublicationBase MetadataBase { get; init; }
    }

    private sealed record PublicationPreviewSession(NpgsqlConnection Connection, NpgsqlTransaction Transaction,
        PublicationPreviewContext Context);

    public async Task<LayerPublicationPreview> PreviewPublicationAsync(
        string connectionString, LayerPublishRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(request);
        using var pinnedSchema = OpenCommittedSchemaScope(connectionString,
            request.ConnectionEndpointFingerprint, request.CommittedSinkPublication, request.Schema, request.Table);
        ValidateSourceBackedPreviewRequest(request);
        var (graph, etag) = await LoadPreviewBaseAsync(cancellationToken).ConfigureAwait(false);
        var serviceName = NormalizeServiceName(request.ServiceName);
        ValidatePublicationScope(graph, serviceName, ResolvePublicationScope(request.Namespace, _tenantContext?.TenantId));
        await using var connection = new NpgsqlConnection(
            (sessionLockConnections?.Value ?? PostgresSessionLockConnections.Pooled).Resolve(connectionString));
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var importSourceLock = await ImportedDatasetSpatialMetadata.AcquireSessionLockAsync(
            connection, request.Schema.Trim(), request.Table.Trim(), cancellationToken).ConfigureAwait(false);
        var source = await PreparePublicationSourceAsync(connection, new PublicationSourceRequest
        {
            ConnectionString = connectionString, Request = request, ServiceName = serviceName
        }, cancellationToken).ConfigureAwait(false);
        return await ReadPublicationPreviewAsync(connection, new PublicationPreviewContext
        {
            Request = request, Source = source, Graph = graph,
            MetadataBase = new LayerPublicationBase(graph.Revision, etag)
        }, cancellationToken).ConfigureAwait(false);
    }

    private void ValidateSourceBackedPreviewRequest(LayerPublishRequest request)
    {
        ValidatePublicationRequest(request);
        if (request.StorageMode == LayerStorageMode.Managed || request.CreateEditableCopy)
        {
            throw new LayerPublishingException(LayerPublishingErrorKind.Validation,
                "Publication preview requires source-backed storage.");
        }
    }

    private async Task<(MetadataV2Graph Graph, string? Etag)> LoadPreviewBaseAsync(CancellationToken cancellationToken)
    {
        var (graph, etag) = await LoadCurrentOrEmptyGraphAsync(cancellationToken).ConfigureAwait(false);
        if (etag is not null)
        {
            return (graph, etag);
        }
        var bootstrap = await _metadataGraphStore.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        return (bootstrap.Graph, bootstrap.Etag);
    }

    private async Task<LayerPublicationPreview> ReadPublicationPreviewAsync(NpgsqlConnection connection,
        PublicationPreviewContext context, CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(
            System.Data.IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false);
        await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction))
        {
            await readOnly.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        var session = new PublicationPreviewSession(connection, transaction, context);
        await ValidateLockedPreviewSourceAsync(session, cancellationToken).ConfigureAwait(false);
        var plan = await BuildPreviewPlanAsync(session, cancellationToken).ConfigureAwait(false);
        var planned = BuildPublishedLayerGraph(plan);
        var fingerprint = await ReadPublicationSourceFingerprintAsync(connection, transaction, context.Request,
            context.Source, cancellationToken).ConfigureAwait(false);
        return SelectPublicationPreview(planned, plan, context.MetadataBase) with { SourceFingerprint = fingerprint };
    }

    private async Task ValidateLockedPreviewSourceAsync(PublicationPreviewSession session, CancellationToken cancellationToken)
    {
        var request = session.Context.Request;
        var source = session.Context.Source;
        var schema = request.Schema.Trim();
        var table = request.Table.Trim();
        await ImportedDatasetSpatialMetadata.LockRelationAsync(
            session.Connection, session.Transaction, schema, table, cancellationToken).ConfigureAwait(false);
        var protectedTable = await ResolveTableInfoForValidationAsync(session.Connection, schema, table,
            cancellationToken, session.Transaction).ConfigureAwait(false);
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
        if (await FindExistingLayerAsync(session.Connection, session.Transaction, schema, table, cancellationToken)
                .ConfigureAwait(false) is not null)
        {
            throw new LayerPublishingException(LayerPublishingErrorKind.Conflict,
                $"Layer already exists for table '{schema}.{table}'.");
        }
    }

    private async Task<PublishedLayerGraphPlan> BuildPreviewPlanAsync(PublicationPreviewSession session,
        CancellationToken cancellationToken)
    {
        var context = session.Context;
        var request = context.Request;
        var source = context.Source;
        var schema = request.Schema.Trim();
        var table = request.Table.Trim();
        var storage = PublishedLayerStorage.ForSourceTable(schema, table, source.PrimaryKeyColumn!.Name,
            source.GeometryColumn, source.StorageSrid,
            source.SelectedColumns.Where(column => column.DataType.Equals("smallint", StringComparison.OrdinalIgnoreCase))
                .Select(column => column.Name).ToArray());
        var epoch = await ResolveImportedStorageEpochAsync(session.Connection, session.Transaction, request,
            schema, table, source.GeometryColumn, source.StorageSrid, storage.StorageSrid,
            cancellationToken, readOnly: true).ConfigureAwait(false);
        var extent = await ReadLayerExtentAsync(session.Connection, session.Transaction, schema, table, source.GeometryColumn,
            source.StorageSrid, managedLayerId: null, cancellationToken).ConfigureAwait(false);
        return CreatePreviewPlan(context, storage) with { StorageCoordinateEpoch = epoch, Extent = extent };
    }

    private PublishedLayerGraphPlan CreatePreviewPlan(PublicationPreviewContext context, PublishedLayerStorage storage)
    {
        var source = context.Source;
        var request = context.Request;
        return new PublishedLayerGraphPlan
        {
            Graph = context.Graph, Request = request, ServiceName = NormalizeServiceName(request.ServiceName),
            LayerId = FindPreviewPlaceholder(context.Graph, request.ConnectionId), Storage = storage,
            PrimaryKey = source.PrimaryKeyColumn!.Name, GeometryColumn = source.GeometryColumn,
            GeometryType = source.GeometryType, Srid = source.Srid, Fields = source.Fields,
            Capabilities = ResolvePublicationCapabilities(request.Capabilities, managedStore: false),
            Scope = ResolvePublicationScope(request.Namespace, _tenantContext?.TenantId), GeneratedAt = DateTimeOffset.UtcNow
        };
    }

    private static int FindPreviewPlaceholder(MetadataV2Graph graph, Guid? connectionId)
    {
        // In-memory only: never advance the SQL sequence or advertise an allocated route.
        var placeholder = int.MaxValue;
        while (graph.Resources.Any(resource => resource.Metadata.Id == BuildResourceId(placeholder, connectionId)) ||
               graph.StorageBindings.Any(binding => binding.Metadata.Id == BuildStorageBindingId(placeholder, connectionId)))
        {
            placeholder--;
        }
        return placeholder;
    }

    private static LayerPublicationPreview SelectPublicationPreview(MetadataV2Graph planned, PublishedLayerGraphPlan plan,
        LayerPublicationBase metadataBase)
    {
        var resourceId = BuildResourceId(plan.LayerId, plan.Request.ConnectionId);
        var bindingId = BuildStorageBindingId(plan.LayerId, plan.Request.ConnectionId);
        var publications = planned.Publications.Where(candidate => candidate.ResourceId == resourceId).ToArray();
        // Return only this plan's objects, never unrelated graph objects or connection secrets.
        return new LayerPublicationPreview
        {
            SourceFingerprint = string.Empty,
            MetadataBase = metadataBase,
            Service = planned.Services.Single(candidate => candidate.Metadata.Id == publications[0].ServiceId),
            Resource = planned.Resources.Single(candidate => candidate.Metadata.Id == resourceId),
            StorageBinding = planned.StorageBindings.Single(candidate => candidate.Metadata.Id == bindingId),
            Publications = publications
        };
    }
}
