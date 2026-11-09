// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Metadata.Services;
using Honua.Core.Features.Portal.Domain;

namespace Honua.Core.Features.Catalog;

public sealed class CatalogGraphProjection(PublishedMetadataProjection metadataProjection, ICoordinateTransformService coordinateTransform)
{
    public async Task<IReadOnlyList<CatalogItem>> ProjectAsync(PublishedMetadataRequest request,
        IReadOnlyList<CatalogProtocolDescriptor> protocols, CancellationToken cancellationToken)
    {
        var items = new List<CatalogItem>();
        var endpoints = new CatalogEndpointLinks(request, protocols);
        foreach (var service in request.Snapshot.Index.ServicesById.Values)
        {
            var visible = metadataProjection.ProjectDiscoverableService(request, service);
            if (visible is null) continue;
            var layers = new List<CatalogItem>();
            foreach (var group in visible.Publications.GroupBy(layer => layer.ResourceId, StringComparer.Ordinal))
            {
                var layer = group.FirstOrDefault(candidate => candidate.IsPrimary) ?? group.First();
                var dataset = request.Snapshot.ResolveResource(layer)!;
                var item = FromMetadata(dataset.Metadata) with
                {
                    Id = layer.Metadata.Id, Title = layer.TitleOverride ?? dataset.Metadata.Title ?? dataset.Metadata.Name,
                    Revision = Fingerprint(request.Snapshot, layer),
                    Access = metadataProjection.DeriveAccessTier(dataset.AccessPolicy, service.AccessPolicy),
                    Folder = Folder(request.Snapshot, service), Endpoints = endpoints.Resolve(group.ToArray()),
                    Spatial = dataset.Spatial?.GeometryType is not null, Extent = await ExtentAsync(dataset, cancellationToken).ConfigureAwait(false),
                    TemporalExtent = dataset.Temporal?.Extent
                };
                layers.Add(item);
            }
            if (visible.Publications.Count > 0 && layers.Count == 0) continue;
            items.Add(FromMetadata(service.Metadata) with
            {
                Kind = "service", Access = visible.Access, Folder = Folder(request.Snapshot, service),
                Revision = ServiceRevision(service, layers),
                Spatial = layers.Any(layer => layer.Spatial), Extent = UnionExtent(layers), TemporalExtent = UnionTime(layers),
                Endpoints = layers.SelectMany(item => item.Endpoints).Distinct().ToArray()
            });
            items.AddRange(layers);
        }
        return items;
    }

    private static string ServiceRevision(MetadataV2Service service, IReadOnlyList<CatalogItem> layers)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(service, MetadataV2JsonContext.Default.MetadataV2Service));
        foreach (var layer in layers.OrderBy(layer => layer.Id, StringComparer.Ordinal))
            hash.AppendData(Encoding.UTF8.GetBytes(layer.Revision));
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static MetadataV2Bbox? UnionExtent(IReadOnlyList<CatalogItem> layers)
    {
        var extents = layers.Where(layer => layer.Extent is not null).Select(layer => layer.Extent!).ToArray();
        if (extents.Length == 0) return null;
        var union = extents.Select(extent => BoundingBox.Create(extent.West, extent.South, extent.East, extent.North, 4326))
            .Aggregate((left, right) => left.Union(right));
        return new() { West = union.MinX, South = union.MinY, East = union.MaxX, North = union.MaxY };
    }

    private static MetadataV2TimeRange? UnionTime(IReadOnlyList<CatalogItem> layers)
    {
        var times = layers.Where(layer => layer.TemporalExtent is not null).Select(layer => layer.TemporalExtent!).ToArray();
        return times.Length == 0 ? null : new()
        {
            Start = times.Any(time => time.Start is null) ? null : times.Min(time => time.Start),
            End = times.Any(time => time.End is null) ? null : times.Max(time => time.End)
        };
    }

    private static CatalogItem FromMetadata(MetadataV2ObjectMetadata metadata) => new()
    {
        Kind = "layer", Id = metadata.Id, Title = metadata.Title ?? metadata.Name,
        Summary = metadata.Description, Description = metadata.Description,
        Access = PortalAccessLevels.Private, Owner = PublishedMetadataProjection.ResolveOwner(metadata),
        Created = metadata.CreatedAt, Updated = metadata.UpdatedAt ?? metadata.CreatedAt,
        Tags = metadata.Tags.Concat(metadata.Keywords).Where(tag => !string.IsNullOrWhiteSpace(tag)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
        DetailHref = string.Empty, Revision = string.Empty
    };

    private static CatalogFolder? Folder(MetadataV2GraphSnapshot snapshot, MetadataV2Service service)
    {
        var folder = snapshot.Graph.Folders.FirstOrDefault(folder => !folder.Deleted && folder.Metadata.Id == service.FolderId &&
            MetadataV2ServicePaths.SameTenant(folder.Metadata.Tenant, service.Metadata.Tenant));
        return folder is null ? null : new(folder.Metadata.Id, folder.Metadata.Title ?? folder.Metadata.Name);
    }

    private async Task<MetadataV2Bbox?> ExtentAsync(MetadataV2Resource dataset, CancellationToken cancellationToken)
    {
        if (dataset.Spatial?.Bbox is not { } bbox || dataset.ReadSrid() is not { } srid) return null;
        if (srid == 4326) return bbox;
        var extent = await coordinateTransform.TransformExtentAsync(bbox.West, bbox.South, bbox.East, bbox.North,
            srid, 4326, cancellationToken).ConfigureAwait(false);
        return extent is { } transformed ? new() { West = transformed.MinX, South = transformed.MinY, East = transformed.MaxX, North = transformed.MaxY } : null;
    }

    private static string Fingerprint(MetadataV2GraphSnapshot snapshot, MetadataV2Publication layer)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            JsonSerializer.Serialize(writer, layer, MetadataV2JsonContext.Default.MetadataV2Publication);
            JsonSerializer.Serialize(writer, snapshot.ResolveResource(layer), MetadataV2JsonContext.Default.MetadataV2Resource);
            JsonSerializer.Serialize(writer, snapshot.Index.ServicesById[layer.ServiceId], MetadataV2JsonContext.Default.MetadataV2Service);
            writer.WriteEndArray();
        }
        return Convert.ToHexStringLower(SHA256.HashData(buffer.ToArray()));
    }
}
