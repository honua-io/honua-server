// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json.Nodes;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Exports;

namespace Honua.Server.Features.Admin.Exports;

internal sealed record ExportVersionRun(
    string PublicationId,
    string Version,
    string CaptureId,
    DateTimeOffset CapturedAt);

internal sealed record StoredPair(
    PmtilesExportArtifact Pmtiles,
    GeoparquetExportArtifact Geoparquet);

internal static class ExportVersionFactory
{
    public static string PmtilesFileName(ExportVersionRun run) => $"{run.PublicationId}.pmtiles";

    public static string GeoparquetFileName(ExportVersionRun run) => $"{run.PublicationId}.parquet";

    public static string NewVersion(DateTimeOffset capturedAt, string captureId)
        => string.Create(CultureInfo.InvariantCulture, $"{capturedAt:yyyyMMdd'T'HHmmss'Z'}-{captureId[^8..]}");

    public static ExportVersion Create(
        ExportVersionRun run,
        ExportLayerSource source,
        ExportFeatureCapture capture,
        ExportFields fields,
        (StoredPair Artifacts, string IdentityField, ExportValidation Validation) published)
    {
        var resource = source.Resource;
        var srid = resource.ReadSrid() ?? 4326;
        var title = string.IsNullOrWhiteSpace(resource.Metadata.Title) ? null : resource.Metadata.Title.Trim();
        return new ExportVersion
        {
            PublicationId = run.PublicationId,
            Version = run.Version,
            Title = title,
            PublishedAt = published.Validation.ValidatedAt,
            SourceLayer = new ExportSourceLayer(source.Published.Service.Metadata.Id, resource.Metadata.Id)
            {
                Title = title
            },
            Capture = new ExportCapture(run.CaptureId, run.CapturedAt, source.Revision, capture.Features.Items.Length),
            Bounds = capture.Bounds,
            Crs = new ExportCrs(string.Create(CultureInfo.InvariantCulture, $"EPSG:{srid}"), "OGC:CRS84"),
            Fields = fields.Declared,
            Identity = new ExportIdentity(published.IdentityField, published.IdentityField, published.IdentityField),
            Geometry = new ExportGeometry("geometry", "wkb", capture.GeometryTypes),
            Artifacts = new ExportArtifacts(published.Artifacts.Pmtiles, published.Artifacts.Geoparquet),
            Validation = published.Validation
        };
    }

    // The Version's one document: a STAC collection that also carries the Dataset's description and licence,
    // links its README.md and AGENTS.md, lists the style and thumbnail as assets and names who produced and hosts the data.
    public static JsonObject Collection(ExportCompanionInput companions)
    {
        var license = companions.Metadata.License;
        var collection = ExportCollectionDocument.Write(companions.Descriptor, new ExportCollectionMetadata(
            companions.Description, string.IsNullOrWhiteSpace(license) ? null : license.Trim()));
        collection["providers"] = ExportStacFacts.Providers(companions);
        var links = collection["links"]!.AsArray();
        links.Add(Link("agents", ExportCompanionFiles.AgentsFileName));
        links.Add(Link("describedby", ExportCompanionFiles.ReadmeFileName));
        var assets = collection["assets"]!.AsObject();
        assets["style"] = Asset(ExportCompanionFiles.StyleFileName, ExportCompanionFiles.StyleMediaType, "style");
        if (companions.Thumbnail is not null)
        {
            assets["thumbnail"] = Asset(ExportThumbnail.FileName, ExportThumbnail.MediaType, "thumbnail");
        }

        return collection;
    }

    // Portolan wants the plain markdown media type on these links.
    private static JsonObject Link(string rel, string href) => new()
    {
        ["rel"] = rel,
        ["href"] = href,
        ["type"] = "text/markdown"
    };

    private static JsonObject Asset(string href, string mediaType, string role) => new()
    {
        ["href"] = href,
        ["type"] = mediaType,
        ["roles"] = new JsonArray(role)
    };
}
