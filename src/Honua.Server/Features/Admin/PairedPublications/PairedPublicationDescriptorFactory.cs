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
            Validation = published.Validation
        };
    }

    // The Version's one document: a STAC collection that also carries the Dataset's description and licence.
    public static JsonObject Collection(PublicationDescriptor descriptor, PairedPublicationSource source)
    {
        var metadata = source.Resource.Metadata;
        return ExportCollectionDocument.Write(descriptor, new ExportCollectionMetadata(
            string.IsNullOrWhiteSpace(metadata.Description) ? null : metadata.Description.Trim(),
            string.IsNullOrWhiteSpace(metadata.License) ? null : metadata.License.Trim()));
    }
}
