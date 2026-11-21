// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Styling;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Core.Features.Styling.Domain;

namespace Honua.Server.Features.Styling;

internal sealed partial class OgcStyleProjection
{
    private const int MaxImpactAssociations = 200;
    private const int MaxImpactConsumers = 100;

    private static OgcStylesheet? Stamp(OgcStylesheet? sheet, int storedVersion, string canonicalMapLibreJson) =>
        sheet is null ? null : sheet with { StoredVersion = storedVersion, CanonicalMapLibreJson = canonicalMapLibreJson };

    public async Task<OgcStyleWriteTarget?> GetWriteTargetAsync(string styleId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(styleId);

        // Same routing as UpdateStyleAsync: a collection style, then a mirrored layer default, then the catalog.
        var (resource, storageLayerId, _) = await ResolveStyledResourceAsync(styleId, cancellationToken).ConfigureAwait(false);
        if (resource is null || !storageLayerId.HasValue)
        {
            var bound = await ResolveMirroredLayerStyleAsync(styleId, cancellationToken).ConfigureAwait(false);
            if (bound is null)
            {
                return await GetCatalogWriteTargetAsync(styleId, cancellationToken).ConfigureAwait(false);
            }

            storageLayerId = bound.Value.StorageLayerId;
        }

        var layerId = storageLayerId.Value;
        var version = await _styleCatalog.GetLayerStyleWriteVersionAsync(layerId, cancellationToken).ConfigureAwait(false);
        return version is null
            ? null
            : new OgcStyleWriteTarget
            {
                LockKey = "layer:" + layerId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Version = version.Value
            };
    }

    private async Task<OgcStyleWriteTarget?> GetCatalogWriteTargetAsync(string styleId, CancellationToken cancellationToken)
    {
        var style = _independentStyleCatalog is null
            ? null
            : await _independentStyleCatalog.GetStyleAsync(styleId, cancellationToken).ConfigureAwait(false);
        return style is null ? null : new OgcStyleWriteTarget { LockKey = "style:" + styleId, Version = style.StyleVersion };
    }

    private Task<LayerStyleUpdateResult> StoreLayerStyleAsync(
        MetadataV2Object resource,
        LayerStyleDocument document,
        CancellationToken cancellationToken)
        => document.ExpectedVersion is { } expected
            ? _styleService.UpdateMapLibreStyleIfVersionAsync(
                resource,
                new ConditionalLayerStyle { LayerId = document.LayerId, MapLibreStyle = document.MapLibreStyle, ExpectedVersion = expected },
                cancellationToken)
            : _styleService.UpdateStyleAsync(resource, document.LayerId, document.MapLibreStyle, drawingInfo: null, cancellationToken: cancellationToken);

    private static OgcStyleUpdateResult StaleWrite() =>
        new(OgcStyleUpdateStatus.PreconditionFailed, "The style changed while it was being saved. Reload it and review the current impact before saving.");

    // A conditional catalog write that stored nothing lost to a concurrent save; an unconditional one found no style.
    private static OgcStyleUpdateResult NothingStored(string styleId, OgcStyleWriteOptions options) =>
        options.ExpectedVersion.HasValue
            ? StaleWrite()
            : new OgcStyleUpdateResult(OgcStyleUpdateStatus.NotFound, $"Style '{styleId}' not found.");

    private sealed record LayerStyleDocument
    {
        public required int LayerId { get; init; }

        public required JsonElement MapLibreStyle { get; init; }

        public int? ExpectedVersion { get; init; }
    }

    private sealed record CatalogStyleDocument
    {
        public required StyleCatalogRecord Existing { get; init; }

        public required string MapLibreStyleJson { get; init; }

        public int? ExpectedVersion { get; init; }
    }

    public async Task<SharedStyleImpact?> GetSharedStyleImpactAsync(
        SharedStyleImpactRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var styleId = request.StyleId;
        var sheet = await GetPublishedStylesheetAsync(styleId, OgcStyleEncoding.MapboxStyle, cancellationToken)
            .ConfigureAwait(false);
        if (sheet?.CanonicalMapLibreJson is not { Length: > 0 } canonical)
        {
            return null;
        }

        var listed = await ListVisibleAssociationsAsync(request, cancellationToken).ConfigureAwait(false);
        var associationsComplete = listed.Count <= MaxImpactAssociations;
        var associations = associationsComplete ? listed : listed.Take(MaxImpactAssociations).ToArray();
        var consumerScan = request.ConsumerIndex is null
            ? new SharedStyleConsumerScan { Consumers = [], Complete = false }
            : await request.ConsumerIndex.ListConsumersAsync(
                new SharedStyleConsumerQuery { StyleId = styleId, Limit = MaxImpactConsumers, Tenant = request.ConsumerTenant },
                cancellationToken).ConfigureAwait(false);
        var contentHash = SharedStyleIdentity.ContentHash(canonical);
        var impact = new SharedStyleImpact
        {
            StyleId = styleId,
            StoredVersion = sheet.StoredVersion,
            ReportedVersion = SharedStyleIdentity.ReportedVersion(sheet.StoredVersion),
            EntityTag = SharedStyleIdentity.EntityTag(sheet.StoredVersion, contentHash),
            ContentHash = contentHash,
            ImpactHash = string.Empty,
            ImpactComplete = associationsComplete && consumerScan.Complete,
            Associations = associations,
            Consumers = consumerScan.Consumers
        };
        return impact with { ImpactHash = SharedStyleIdentity.ImpactHash(impact) };
    }

    private async Task<IReadOnlyList<SharedStyleAssociation>> ListVisibleAssociationsAsync(
        SharedStyleImpactRequest request,
        CancellationToken cancellationToken)
    {
        var styleId = request.StyleId;
        var found = new AssociationList(await _graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false), request);
        if (_independentStyleCatalog is not null)
        {
            foreach (var link in await _independentStyleCatalog.ListAssociationsAsync(cancellationToken).ConfigureAwait(false))
            {
                if (string.Equals(link.StyleId, styleId, StringComparison.Ordinal))
                {
                    found.Add(link.LayerId);
                }
            }
        }

        foreach (var resource in found.Snapshot.Graph.Resources)
        {
            if (!string.Equals(resource.Metadata.Name, styleId, StringComparison.Ordinal))
            {
                continue;
            }

            var storageLayerId = found.Snapshot.ResolveStorageLayerId(resource);
            if (storageLayerId.HasValue)
            {
                found.Add(storageLayerId.Value);
            }
        }

        return found.Associations;
    }

    private sealed class AssociationList
    {
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

        private readonly SharedStyleImpactRequest _request;

        public AssociationList(MetadataV2GraphSnapshot snapshot, SharedStyleImpactRequest request)
        {
            Snapshot = snapshot;
            _request = request;
        }

        public MetadataV2GraphSnapshot Snapshot { get; }

        public List<SharedStyleAssociation> Associations { get; } = [];

        public void Add(int layerId)
        {
            if (!Snapshot.Index.ResourcesByStorageLayerId.TryGetValue(layerId, out var resource))
            {
                AddUnresolved(layerId);
                return;
            }

            if (!MetadataV2TenantVisibility.IsVisibleToTenant(resource, _request.RequestTenant))
            {
                return;
            }

            AddResource(resource);
        }

        // A layer id the graph does not hold has no tenant to check; only an unscoped caller sees it.
        private void AddUnresolved(int layerId)
        {
            if (_request.ConsumerTenant is not null)
            {
                return;
            }

            var fallback = layerId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (_seen.Add(fallback))
            {
                Associations.Add(new SharedStyleAssociation
                {
                    ResourceId = fallback,
                    Name = "layer " + fallback
                });
            }
        }

        private void AddResource(MetadataV2Object resource)
        {
            var resourceId = string.IsNullOrEmpty(resource.Metadata.Id) ? resource.Metadata.Name : resource.Metadata.Id;
            if (!_seen.Add(resourceId))
            {
                return;
            }

            string? serviceId = null;
            if (Snapshot.Index.ServiceNamesByResource.TryGetValue(resource.Metadata.Id, out var names) && names.Length > 0)
            {
                serviceId = names[0];
            }

            Associations.Add(new SharedStyleAssociation
            {
                ResourceId = resourceId,
                Name = string.IsNullOrEmpty(resource.Metadata.Name) ? resourceId : resource.Metadata.Name,
                Title = resource.Metadata.Title,
                ServiceId = serviceId
            });
        }
    }
}
