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

    /// <summary>
    /// Rebuild a published layer's canonical feature snapshot (<c>honua.features</c>) from
    /// its live source table.
    /// </summary>
    /// <remarks>
    /// Publishing materializes a one-time snapshot of the source table into the canonical
    /// <c>features</c> table that the server-side MVT/tile path reads, while the feature-query
    /// paths read the live source table. Re-importing or otherwise updating the source leaves
    /// that snapshot stale until it is rebuilt, so callers invoke this after a source mutation
    /// to keep tiles consistent with the live data.
    /// </remarks>
    /// <param name="connectionString">PostgreSQL connection string.</param>
    /// <param name="layerId">Identifier of the published layer to refresh.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The refresh result, or <see langword="null"/> when no such published layer exists.</returns>
    Task<MaterializedFeatureRefreshResult?> RefreshMaterializedFeaturesAsync(
        string connectionString,
        int layerId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Rebuild the canonical feature snapshot for every published layer backed by the given
    /// source table, from that live source table.
    /// </summary>
    /// <remarks>
    /// Used by the import path, which knows the source <c>schema.table</c> it just (re)loaded
    /// but not the layer ids. See <see cref="RefreshMaterializedFeaturesAsync(string,int,CancellationToken)"/>
    /// for why the snapshot must be rebuilt after a source mutation.
    /// </remarks>
    /// <param name="connectionString">PostgreSQL connection string.</param>
    /// <param name="schema">Source schema of the (re)loaded table. When <see langword="null"/>
    /// or whitespace, every published layer backed by a table with the given name is refreshed
    /// regardless of schema (the import path may not know the resolved target schema).</param>
    /// <param name="table">Source table that was (re)loaded.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>One result per refreshed layer; empty when the table backs no published layer.</returns>
    Task<IReadOnlyList<MaterializedFeatureRefreshResult>> RefreshMaterializedFeaturesForSourceTableAsync(
        string connectionString,
        string? schema,
        string table,
        CancellationToken cancellationToken = default);
}
