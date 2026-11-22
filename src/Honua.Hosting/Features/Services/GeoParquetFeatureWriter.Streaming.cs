// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Apache.Arrow;
using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;

namespace Honua.Infrastructure.Services;

public static partial class GeoParquetFeatureWriter
{
    public static async Task<(Stream response, string contentType)> FormatAsGeoParquetStreamAsync(
        IAsyncEnumerable<Feature> features,
        MetadataV2Object resource,
        GeoParquetStreamEncodingOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(features);
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(options);

        var limits = options.EncodingLimits ?? new GeoParquetLimits();
        ValidateEncodingLimits(limits);

        var includeGeometry = options.ReturnGeometry && resource.HasGeometry();
        var srid = options.OutputSrid ?? resource.ReadSrid() ?? SpatialReference.WGS84.Wkid;
        _ = ResolveCoordinateEpoch(resource, includeGeometry, srid);
        _ = ResolveGeoParquetCrsProjJson(includeGeometry, srid);
        EnsureSupportedCloudNativeGeometryMeasures(includeGeometry, options.ReturnM, "GeoParquet");

        var encoding = new StreamingEncoding
        {
            Resource = resource,
            Options = options,
            Limits = limits,
            IncludeGeometry = includeGeometry,
            Srid = srid
        };

        using var ordering = limits.OrderByBboxHilbert && includeGeometry
            ? new ExternalHilbertOrder(encoding) : null;
        if (ordering is not null)
        {
            encoding = encoding with { Scratch = ordering.Scratch };
        }

        var session = new StreamingParquetSession(encoding);
        try
        {
            if (ordering is not null)
            {
                await ordering.PrepareAsync(features, session.InspectSpillFeature, cancellationToken).ConfigureAwait(false);
                features = ordering.ReadOrderedAsync(cancellationToken);
            }

            await session.WriteAllAsync(features, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return (session.Complete(), ContentType);
        }
        catch (Exception ex)
        {
            throw session.AbortAndTranslate(ex);
        }
    }

    private sealed record StreamingEncoding
    {
        public required MetadataV2Object Resource { get; init; }
        public required GeoParquetStreamEncodingOptions Options { get; init; }
        public required GeoParquetLimits Limits { get; init; }
        public required bool IncludeGeometry { get; init; }
        public required int Srid { get; init; }
        public ScratchBudget? Scratch { get; init; }
    }

    private readonly record struct GeneratedColumns(string? Geometry, string? Bbox);

    private static Dictionary<string, int> MapUpgradeColumns(
        Schema sourceSchema, GeneratedColumns previous, GeneratedColumns current)
    {
        var columns = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < sourceSchema.FieldsList.Count; i++)
        {
            var name = sourceSchema.FieldsList[i].Name;
            if (name == previous.Geometry)
            {
                columns.Add(current.Geometry!, i);
            }
            else if (name == previous.Bbox)
            {
                if (current.Bbox is not null)
                {
                    columns.Add(current.Bbox, i);
                }
            }
            else
            {
                columns.Add(name, i);
            }
        }

        return columns;
    }
}
