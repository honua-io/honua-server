// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Infrastructure.Services;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Server.Features.Admin.PairedPublications;

// One read of the source layer in WGS 84, shared by the GeoParquet artifact and the descriptor.
internal sealed class PairedPublicationCapture
{
    internal const string CaptureIdMetadataKey = "honua:captureId";
    internal const string PublicationIdMetadataKey = "honua:publicationId";
    internal const string VersionMetadataKey = "honua:publicationVersion";
    private const int PageSize = 5000;
    private const int MaxFeatures = 500_000;

    private PairedPublicationCapture(
        QueryResult<Feature> features,
        (double[] Bounds, IReadOnlyList<string> GeometryTypes) extent,
        ImmutableArray<string> undeclaredAttributes)
    {
        Features = features;
        Bounds = extent.Bounds;
        GeometryTypes = extent.GeometryTypes;
        UndeclaredAttributes = undeclaredAttributes;
    }

    public QueryResult<Feature> Features { get; }

    public double[] Bounds { get; }

    public IReadOnlyList<string> GeometryTypes { get; }

    // Stored attribute keys with no public field; tiles must withhold them as GeoParquet does.
    public ImmutableArray<string> UndeclaredAttributes { get; }

    public static async Task<PairedPublicationCapture> ReadAsync(
        IFeatureReader reader,
        int storageLayerId,
        PairedPublicationFields fields,
        CancellationToken cancellationToken)
    {
        var features = ImmutableArray.CreateBuilder<Feature>();
        for (var offset = 0; ; offset += PageSize)
        {
            var page = await reader.QueryAsync(storageLayerId, PageQuery(fields, offset), cancellationToken).ConfigureAwait(false);
            features.AddRange(page.Items);
            if (features.Count > MaxFeatures)
            {
                throw new PairedPublicationException($"Paired publish supports at most {MaxFeatures} features per layer.");
            }

            if (page.Items.Length < PageSize)
            {
                break;
            }
        }

        RequireUniqueIdentity(features, fields.IdentityField);
        var undeclared = features
            .SelectMany(feature => feature.Attributes.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(key => !fields.IsPublic(key))
            .ToImmutableArray();
        return new PairedPublicationCapture(
            QueryResult<Feature>.Create(features.Count, features.ToImmutable()), Measure(features), undeclared);
    }

    // A rendered feature resolves to exactly one GeoParquet row through this value.
    private static void RequireUniqueIdentity(IEnumerable<Feature> features, string identityField)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var feature in features)
        {
            var value = feature.Attributes.TryGetValue(identityField, out var raw) ? raw?.ToString() : null;
            if (string.IsNullOrEmpty(value) || !seen.Add(value))
            {
                throw new PairedPublicationException(
                    $"Identity field '{identityField}' must hold a unique, non-empty value for every feature.");
            }
        }
    }

    public byte[] EncodeGeoParquet(
        MetadataV2Resource resource,
        PairedPublicationFields fields,
        LimitsOptions limits,
        IReadOnlyDictionary<string, string> fileMetadata)
    {
        var (payload, _) = GeoParquetFeatureWriter.FormatAsGeoParquet(
            Features,
            resource,
            fields.ObjectIdField,
            returnGeometry: true,
            SpatialReference.WGS84.Wkid,
            returnZ: false,
            returnM: false,
            limits.Geometry,
            [.. fields.ParquetFields],
            encodingLimits: limits.GeoParquet,
            fileMetadata: fileMetadata);
        return payload.ToArray();
    }

    private static FeatureQuery PageQuery(PairedPublicationFields fields, int offset) => new()
    {
        EnforcedMaskedFields = fields.Hidden,
        OutputSrid = SpatialReference.WGS84.Wkid,
        OrderBy = [new OrderByClause { Field = fields.ObjectIdField }],
        Offset = offset,
        Limit = PageSize
    };

    private static (double[] Bounds, IReadOnlyList<string> GeometryTypes) Measure(IEnumerable<Feature> features)
    {
        var reader = new WKBReader();
        var envelope = new Envelope();
        var types = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var feature in features)
        {
            if (feature.Geometry is not { Length: > 0 } wkb)
            {
                continue;
            }

            var geometry = reader.Read(wkb);
            if (!geometry.IsEmpty)
            {
                envelope.ExpandToInclude(geometry.EnvelopeInternal);
                types.Add(geometry.GeometryType);
            }
        }

        if (envelope.IsNull || envelope.Width <= 0 || envelope.Height <= 0)
        {
            throw new PairedPublicationException("Paired publish needs features whose geometries cover an area.");
        }

        return ([envelope.MinX, envelope.MinY, envelope.MaxX, envelope.MaxY], types.ToList());
    }
}
