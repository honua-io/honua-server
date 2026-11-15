// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json.Nodes;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Publishing.Descriptors;

namespace Honua.Server.Features.Admin.PairedPublications;

internal sealed record PairedPublicationRun(
    string PublicationId,
    string Version,
    string CaptureId,
    DateTimeOffset CapturedAt);

internal sealed record StoredPair(
    PmtilesPublicationArtifact Pmtiles,
    GeoparquetPublicationArtifact Geoparquet);

internal static class PairedPublicationDescriptorFactory
{
    internal const string DescriptorFileName = "descriptor.json";
    internal const string StacItemFileName = "item.json";

    public static string PmtilesFileName(PairedPublicationRun run) => $"{run.PublicationId}.pmtiles";

    public static string GeoparquetFileName(PairedPublicationRun run) => $"{run.PublicationId}.parquet";

    public static string NewVersion(DateTimeOffset capturedAt, string captureId)
        => string.Create(CultureInfo.InvariantCulture, $"{capturedAt:yyyyMMdd'T'HHmmss'Z'}-{captureId[^8..]}");

    public static PublicationDescriptor Create(
        PairedPublicationRun run,
        PairedPublicationSource source,
        PairedPublicationCapture capture,
        PairedPublicationFields fields,
        (StoredPair Artifacts, string IdentityField, PublicationValidation Validation) published)
    {
        var resource = source.Resource;
        var srid = resource.ReadSrid() ?? 4326;
        var title = string.IsNullOrWhiteSpace(resource.Metadata.Title) ? null : resource.Metadata.Title.Trim();
        return new PublicationDescriptor
        {
            PublicationId = run.PublicationId,
            Version = run.Version,
            Title = title,
            PublishedAt = published.Validation.ValidatedAt,
            SourceLayer = new PublicationSourceLayer(source.Published.Service.Metadata.Id, resource.Metadata.Id)
            {
                Title = title
            },
            Capture = new PublicationCapture(run.CaptureId, run.CapturedAt, source.Revision, capture.Features.Items.Length),
            Bounds = capture.Bounds,
            Crs = new PublicationCrs(string.Create(CultureInfo.InvariantCulture, $"EPSG:{srid}"), "OGC:CRS84"),
            Fields = fields.Declared,
            Identity = new PublicationIdentity(published.IdentityField, published.IdentityField, published.IdentityField),
            Geometry = new PublicationGeometry("geometry", "wkb", capture.GeometryTypes),
            Artifacts = new PublicationArtifacts(published.Artifacts.Pmtiles, published.Artifacts.Geoparquet),
            Validation = published.Validation,
            Links = new PublicationLinks(StacItemFileName) { Self = DescriptorFileName }
        };
    }

    // A STAC item with role-tagged assets, so a catalog browser opens the pair without Honua.
    public static JsonObject StacItem(PublicationDescriptor descriptor)
    {
        var (west, south, east, north) = (descriptor.Bounds[0], descriptor.Bounds[1], descriptor.Bounds[2], descriptor.Bounds[3]);
        return new JsonObject
        {
            ["type"] = "Feature",
            ["stac_version"] = "1.1.0",
            ["id"] = $"{descriptor.PublicationId}-{descriptor.Version}",
            ["bbox"] = new JsonArray(west, south, east, north),
            ["geometry"] = new JsonObject
            {
                ["type"] = "Polygon",
                ["coordinates"] = new JsonArray(new JsonArray(
                    new JsonArray(west, south), new JsonArray(east, south), new JsonArray(east, north),
                    new JsonArray(west, north), new JsonArray(west, south)))
            },
            ["properties"] = StacProperties(descriptor),
            ["links"] = new JsonArray(new JsonObject
            {
                ["rel"] = "describedby",
                ["href"] = DescriptorFileName,
                ["type"] = "application/json"
            }),
            ["assets"] = new JsonObject
            {
                ["pmtiles"] = Asset(descriptor.Artifacts.Pmtiles.Href, PmtilesPublicationArtifact.MediaTypeValue, "visual"),
                ["geoparquet"] = Asset(descriptor.Artifacts.Geoparquet.Href, GeoparquetPublicationArtifact.MediaTypeValue, "data")
            }
        };
    }

    private static JsonObject StacProperties(PublicationDescriptor descriptor)
    {
        var properties = new JsonObject { ["datetime"] = descriptor.Capture.CapturedAt.ToString("O", CultureInfo.InvariantCulture) };
        if (descriptor.Title is not null)
        {
            properties["title"] = descriptor.Title;
        }

        return properties;
    }

    private static JsonObject Asset(string href, string mediaType, string role) => new()
    {
        ["href"] = href,
        ["type"] = mediaType,
        ["roles"] = new JsonArray(role)
    };
}
