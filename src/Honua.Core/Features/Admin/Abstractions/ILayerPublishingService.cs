// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Admin.Domain;

namespace Honua.Core.Features.Admin.Abstractions;

public interface ILayerPublishingService
{
    Task<IReadOnlyList<PublishedLayerSummary>> ListPublishedLayersAsync(
        string connectionString,
        string serviceName,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PublishedLayerSummary>> ListPublishedLayersAsync(
        string connectionString,
        string serviceName,
        Guid? connectionId,
        CancellationToken cancellationToken = default)
        => ListPublishedLayersAsync(connectionString, serviceName, cancellationToken);

    Task<PublishedLayerSummary> PublishLayerAsync(
        string connectionString,
        LayerPublishRequest request,
        CancellationToken cancellationToken = default);

    // A zero count means the row count is unknown, and callers then publish synchronously.
    Task<long> CountSourceRowsAsync(
        string connectionString,
        string schema,
        string table,
        CancellationToken cancellationToken = default)
        => Task.FromResult(0L);

    Task<PublishedLayerSummary?> LinkExistingLayerToServiceAsync(
        string connectionString,
        int layerId,
        string serviceName,
        bool enabled,
        CancellationToken cancellationToken = default);

    Task<PublishedLayerSummary?> LinkExistingLayerToServiceAsync(
        string connectionString,
        int layerId,
        string serviceName,
        bool enabled,
        Guid? connectionId,
        CancellationToken cancellationToken = default)
        => LinkExistingLayerToServiceAsync(connectionString, layerId, serviceName, enabled, cancellationToken);

    Task<TablePublishValidationResult> ValidateTableForPublishAsync(
        string connectionString,
        TablePublishValidationRequest request,
        CancellationToken cancellationToken = default);

    Task<PublishedLayerSummary?> SetLayerEnabledAsync(
        string connectionString,
        int layerId,
        string serviceName,
        bool enabled,
        CancellationToken cancellationToken = default);

    Task<PublishedLayerSummary?> SetLayerEnabledAsync(
        string connectionString,
        int layerId,
        string serviceName,
        bool enabled,
        Guid? connectionId,
        CancellationToken cancellationToken = default)
        => SetLayerEnabledAsync(connectionString, layerId, serviceName, enabled, cancellationToken);

    Task<IReadOnlyList<PublishedLayerSummary>> SetServiceLayersEnabledAsync(
        string connectionString,
        string serviceName,
        bool enabled,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PublishedLayerSummary>> SetServiceLayersEnabledAsync(
        string connectionString,
        string serviceName,
        bool enabled,
        Guid? connectionId,
        CancellationToken cancellationToken = default)
        => SetServiceLayersEnabledAsync(connectionString, serviceName, enabled, cancellationToken);

    Task<LayerExtentRefreshResult?> RefreshLayerExtentsAsync(
        string connectionString,
        string serviceName,
        CancellationToken cancellationToken = default);

    Task<LayerExtentRefreshResult?> RefreshLayerExtentsAsync(
        string connectionString,
        string serviceName,
        Guid? connectionId,
        CancellationToken cancellationToken = default)
        => RefreshLayerExtentsAsync(connectionString, serviceName, cancellationToken);

    Task<MaterializedFeatureRefreshResult?> RefreshMaterializedFeaturesAsync(
        string connectionString,
        int layerId,
        CancellationToken cancellationToken = default);

    Task<MaterializedFeatureRefreshResult?> RefreshMaterializedFeaturesAsync(
        string connectionString,
        int layerId,
        Guid? connectionId,
        CancellationToken cancellationToken = default)
        => RefreshMaterializedFeaturesAsync(connectionString, layerId, cancellationToken);

    Task<IReadOnlyList<MaterializedFeatureRefreshResult>> RefreshMaterializedFeaturesForSourceTableAsync(
        string connectionString,
        string? schema,
        string table,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MaterializedFeatureRefreshResult>> RefreshMaterializedFeaturesForSourceTableAsync(
        string connectionString,
        string? schema,
        string table,
        Guid? connectionId,
        CancellationToken cancellationToken = default)
        => RefreshMaterializedFeaturesForSourceTableAsync(connectionString, schema, table, cancellationToken);

    // A replacement import keeps the layer row, so the catalog field metadata must be rewritten or it keeps the previous schema.
    Task<PublishedLayerSummary?> RewritePublishedLayerFieldsAsync(
        string connectionString,
        int layerId,
        LayerPublishRequest request,
        CancellationToken cancellationToken = default)
        => Task.FromResult<PublishedLayerSummary?>(null);
}
