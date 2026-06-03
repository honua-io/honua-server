// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Styling.Abstractions;
using Microsoft.Extensions.Logging;

namespace Honua.Postgres.Features.Metadata;

/// <summary>
/// PostgreSQL-backed <see cref="IMetadataV2StyleGraphSync"/> (ADR-0048, Phase 2,
/// issue #1389). Reconciles the canonical graph's <c>Type=Style</c> resources and
/// <c>StyleResourceIds</c> against the independent style catalog whenever a layer's
/// styles change, so the FeatureServer/MapServer style read path reflects edits
/// immediately rather than waiting for a re-publish.
/// </summary>
internal sealed class PostgresMetadataV2StyleGraphSync : IMetadataV2StyleGraphSync
{
    private readonly IMetadataV2GraphStore _graphStore;

    // Style reconciliation mutates the current graph and saves it, so it loads the base
    // from the persisted snapshot only — never the V1-catalog compat synthesis that
    // GetCurrentAsync performs for serving paths (honua-server#1412). Building on a
    // synthesized-but-unpersisted base would fail SaveAsync's reconciliation.
    private readonly IMetadataV2GraphWriteBaseReader? _writeBaseReader;
    private readonly IStyleCatalog _styleCatalog;
    private readonly ILogger<PostgresMetadataV2StyleGraphSync> _logger;

    public PostgresMetadataV2StyleGraphSync(
        IMetadataV2GraphStore graphStore,
        IStyleCatalog styleCatalog,
        ILogger<PostgresMetadataV2StyleGraphSync> logger)
    {
        _graphStore = graphStore ?? throw new ArgumentNullException(nameof(graphStore));
        _writeBaseReader = graphStore as IMetadataV2GraphWriteBaseReader;
        _styleCatalog = styleCatalog ?? throw new ArgumentNullException(nameof(styleCatalog));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task SyncLayerStylesAsync(int layerId, CancellationToken cancellationToken = default)
    {
        MetadataV2GraphSnapshot snapshot;
        if (_writeBaseReader is not null)
        {
            var persisted = await _writeBaseReader
                .TryGetPersistedCurrentAsync(cancellationToken)
                .ConfigureAwait(false);
            if (persisted is null)
            {
                // No activated snapshot yet (fresh DB / V1-only catalog); nothing to
                // reconcile — styles are served straight from the catalog.
                PostgresMetadataV2StyleGraphSyncLog.NoSnapshot(_logger, layerId);
                return;
            }

            snapshot = persisted;
        }
        else
        {
            try
            {
                snapshot = await _graphStore.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException)
            {
                // No activated snapshot yet (fresh DB); nothing to reconcile.
                PostgresMetadataV2StyleGraphSyncLog.NoSnapshot(_logger, layerId);
                return;
            }
        }

        var graph = snapshot.Graph;
        if (!snapshot.Index.ResourcesByStorageLayerId.TryGetValue(layerId, out var dataResource))
        {
            // The layer is not (yet) represented in the graph; the publish path will
            // pick up its styles when it next runs.
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var styles = await _styleCatalog.GetStylesForLayerAsync(layerId, cancellationToken).ConfigureAwait(false);

        var styleResources = new List<MetadataV2Resource>(styles.Count);
        var styleResourceIds = new List<string>(styles.Count);
        foreach (var style in styles)
        {
            styleResources.Add(MetadataV2StyleResourceFactory.BuildStyleResource(
                style.StyleId,
                style.MapLibreStyleJson,
                style.Title,
                style.Description,
                style.DrawingInfoJson,
                style.StyleVersion,
                style.CreatedAt == default ? now : style.CreatedAt,
                style.UpdatedAt == default ? now : style.UpdatedAt));
            styleResourceIds.Add(MetadataV2StyleResourceFactory.BuildStyleResourceId(style.StyleId));
        }

        // Replace the data resource's StyleResourceIds with the current catalog order.
        var updatedDataResource = dataResource with { StyleResourceIds = styleResourceIds };

        var resources = new List<MetadataV2Resource>(graph.Resources.Count + styleResources.Count);
        foreach (var resource in graph.Resources)
        {
            if (string.Equals(resource.Metadata.Id, updatedDataResource.Metadata.Id, StringComparison.Ordinal))
            {
                resources.Add(updatedDataResource);
            }
            else
            {
                resources.Add(resource);
            }
        }

        // Upsert (add or refresh) the layer's style resources.
        foreach (var styleResource in styleResources)
        {
            var index = resources.FindIndex(r =>
                string.Equals(r.Metadata.Id, styleResource.Metadata.Id, StringComparison.Ordinal));
            if (index >= 0)
            {
                resources[index] = styleResource;
            }
            else
            {
                resources.Add(styleResource);
            }
        }

        // Prune Type=Style resources that are no longer referenced by any resource so
        // a deleted/disassociated style does not linger and trip referential checks.
        var referencedStyleIds = resources
            .SelectMany(r => r.StyleResourceIds)
            .ToHashSet(StringComparer.Ordinal);
        resources.RemoveAll(r =>
            r.Type == MetadataV2ResourceType.Style
            && !referencedStyleIds.Contains(r.Metadata.Id));

        var updatedGraph = graph with
        {
            Revision = Math.Max(graph.Revision + 1, 1),
            GeneratedAt = now,
            Resources = resources
        };

        var validation = MetadataV2GraphValidator.Validate(updatedGraph);
        if (!validation.IsValid)
        {
            PostgresMetadataV2StyleGraphSyncLog.ValidationFailed(
                _logger,
                layerId,
                string.Join("; ", validation.Errors));
            return;
        }

        await _graphStore.SaveAsync(updatedGraph, snapshot.Etag, cancellationToken).ConfigureAwait(false);
        PostgresMetadataV2StyleGraphSyncLog.Synced(_logger, layerId, styleResourceIds.Count);
    }
}

internal static partial class PostgresMetadataV2StyleGraphSyncLog
{
    [LoggerMessage(EventId = 6100, Level = LogLevel.Debug,
        Message = "No activated metadata-v2 snapshot; skipping style graph sync for layer {LayerId}.")]
    public static partial void NoSnapshot(ILogger logger, int layerId);

    [LoggerMessage(EventId = 6101, Level = LogLevel.Warning,
        Message = "Style graph sync produced an invalid graph for layer {LayerId}: {Errors}")]
    public static partial void ValidationFailed(ILogger logger, int layerId, string errors);

    [LoggerMessage(EventId = 6102, Level = LogLevel.Debug,
        Message = "Synced {StyleCount} style resource(s) into the graph for layer {LayerId}.")]
    public static partial void Synced(ILogger logger, int layerId, int styleCount);
}
