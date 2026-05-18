// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Admin.Domain;

namespace Honua.Core.Features.Admin.Abstractions;

/// <summary>
/// Service for publishing PostGIS tables as Honua layers.
/// </summary>
public interface ILayerPublishingService
{
    /// <summary>
    /// List published layers for the specified connection.
    /// </summary>
    /// <param name="connectionString">PostgreSQL connection string.</param>
    /// <param name="serviceName">Service name to evaluate enablement against.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<PublishedLayerSummary>> ListPublishedLayersAsync(
        string connectionString,
        string serviceName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Publish a PostGIS table as a layer.
    /// </summary>
    /// <param name="connectionString">PostgreSQL connection string.</param>
    /// <param name="request">Layer publish request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<PublishedLayerSummary> PublishLayerAsync(
        string connectionString,
        LayerPublishRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Link an existing layer into a service.
    /// </summary>
    /// <param name="connectionString">PostgreSQL connection string.</param>
    /// <param name="layerId">Existing layer identifier.</param>
    /// <param name="serviceName">Service name.</param>
    /// <param name="enabled">Whether the layer should be enabled after linking.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<PublishedLayerSummary?> LinkExistingLayerToServiceAsync(
        string connectionString,
        int layerId,
        string serviceName,
        bool enabled,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Validate a PostGIS table before publishing it as a layer.
    /// </summary>
    /// <param name="connectionString">PostgreSQL connection string.</param>
    /// <param name="request">Table publish validation request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<TablePublishValidationResult> ValidateTableForPublishAsync(
        string connectionString,
        TablePublishValidationRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Enable or disable a layer within a service.
    /// </summary>
    /// <param name="connectionString">PostgreSQL connection string.</param>
    /// <param name="layerId">Layer identifier.</param>
    /// <param name="serviceName">Service name.</param>
    /// <param name="enabled">Whether the layer should be enabled.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<PublishedLayerSummary?> SetLayerEnabledAsync(
        string connectionString,
        int layerId,
        string serviceName,
        bool enabled,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Enable or disable all layers within a service.
    /// </summary>
    /// <param name="connectionString">PostgreSQL connection string.</param>
    /// <param name="serviceName">Service name.</param>
    /// <param name="enabled">Whether the layers should be enabled.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<PublishedLayerSummary>> SetServiceLayersEnabledAsync(
        string connectionString,
        string serviceName,
        bool enabled,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Recompute published layer extents and the containing service extent from source tables.
    /// </summary>
    /// <param name="connectionString">PostgreSQL connection string.</param>
    /// <param name="serviceName">Service name whose layers should be refreshed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<LayerExtentRefreshResult?> RefreshLayerExtentsAsync(
        string connectionString,
        string serviceName,
        CancellationToken cancellationToken = default);
}
