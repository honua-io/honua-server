// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Runtime.CompilerServices;
using System.Text;
using Honua.Core.Features.GeoETL.Abstractions;
using Honua.Core.Features.GeoETL.Domain;
using Honua.Core.Features.Import.Services;
using NetTopologySuite.Features;
using NetTopologySuite.Index.Strtree;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

namespace Honua.Core.Features.GeoETL.Services.Transforms;

/// <summary>
/// Phase 1 spatial-join enrichment transform. For each streaming feature, finds the first
/// reference feature whose geometry intersects (or contains, for point-in-polygon) and
/// copies selected reference attributes onto the feature. The reference set is a GeoJSON
/// FeatureCollection loaded once into an in-memory <see cref="STRtree{T}"/> spatial index.
/// Pure managed NetTopologySuite — no GEOS native dependency. The streamed (left) side
/// stays constant-memory; only the reference (right) side is materialized, which suits the
/// "small lookup polygons, large feature stream" enrichment shape (ADR-0038 delegates
/// large spatial joins to PostGIS).
/// </summary>
/// <remarks>
/// Reference set (one required): <c>referenceInline</c> (a GeoJSON FeatureCollection
/// document) or <c>referencePath</c> (a GeoJSON file path). Optional:
/// <list type="bullet">
/// <item><c>predicate</c> — <c>intersects</c> (default) or <c>contains</c> (the reference
/// geometry must contain the feature, the point-in-polygon case).</item>
/// <item><c>transfer</c> — comma-separated reference attribute names to copy; when omitted
/// all reference attributes are copied.</item>
/// <item><c>prefix</c> — prepended to each transferred attribute name to avoid collisions.
/// </item>
/// <item><c>keepUnmatched</c> — <c>true</c> (default) passes unmatched features through
/// unchanged; <c>false</c> drops them (inner join).</item>
/// </list>
/// </remarks>
public sealed class SpatialJoinTransform : IPipelineTransform
{
    /// <summary>
    /// The transform type discriminator.
    /// </summary>
    public const string TransformType = "spatial-join";

    /// <inheritdoc />
    public string Type => TransformType;

    /// <inheritdoc />
    public async IAsyncEnumerable<IFeature> TransformAsync(
        TransformConfig config,
        IAsyncEnumerable<IFeature> source,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(source);

        var contains = config.Options.TryGetValue("predicate", out var predicate)
            && string.Equals(predicate, "contains", StringComparison.OrdinalIgnoreCase);
        var prefix = config.Options.TryGetValue("prefix", out var rawPrefix) ? rawPrefix : string.Empty;
        var keepUnmatched = !config.Options.TryGetValue("keepUnmatched", out var rawKeep)
            || !bool.TryParse(rawKeep, out var keep) || keep;
        string[]? transfer = config.Options.TryGetValue("transfer", out var rawTransfer)
            && !string.IsNullOrWhiteSpace(rawTransfer)
            ? rawTransfer.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : null;

        var index = await BuildIndexAsync(config, cancellationToken).ConfigureAwait(false);

        await foreach (var feature in source.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var geometry = feature.Geometry;
            if (geometry is null || geometry.IsEmpty)
            {
                if (keepUnmatched)
                {
                    yield return feature;
                }

                continue;
            }

            var match = FindMatch(index, geometry, contains);
            if (match is null)
            {
                if (keepUnmatched)
                {
                    yield return feature;
                }

                continue;
            }

            yield return Enrich(feature, match.Attributes, transfer, prefix);
        }
    }

    private static Feature Enrich(
        IFeature feature,
        IAttributesTable? referenceAttributes,
        string[]? transfer,
        string prefix)
    {
        var merged = new AttributesTable();
        if (feature.Attributes is not null)
        {
            foreach (var name in feature.Attributes.GetNames())
            {
                merged.Add(name, feature.Attributes.GetOptionalValue(name));
            }
        }

        if (referenceAttributes is not null)
        {
            var names = transfer ?? referenceAttributes.GetNames();
            foreach (var name in names)
            {
                if (!referenceAttributes.Exists(name))
                {
                    continue;
                }

                var key = string.IsNullOrEmpty(prefix) ? name : prefix + name;
                if (merged.Exists(key))
                {
                    merged[key] = referenceAttributes.GetOptionalValue(name);
                }
                else
                {
                    merged.Add(key, referenceAttributes.GetOptionalValue(name));
                }
            }
        }

        return new Feature(feature.Geometry, merged);
    }

    private static IFeature? FindMatch(STRtree<IFeature> index, NtsGeometry geometry, bool contains)
    {
        var candidates = index.Query(geometry.EnvelopeInternal);
        foreach (var candidate in candidates)
        {
            var referenceGeometry = candidate.Geometry;
            if (referenceGeometry is null)
            {
                continue;
            }

            var matched = contains
                ? referenceGeometry.Contains(geometry)
                : referenceGeometry.Intersects(geometry);
            if (matched)
            {
                return candidate;
            }
        }

        return null;
    }

    private static async Task<STRtree<IFeature>> BuildIndexAsync(
        TransformConfig config,
        CancellationToken cancellationToken)
    {
        var index = new STRtree<IFeature>();
        var reader = new StreamingGeoJsonReader();

        await using var stream = OpenReferenceStream(config);
        await foreach (var feature in reader.ReadFeaturesAsync(stream, cancellationToken).ConfigureAwait(false))
        {
            if (feature.Geometry is not null && !feature.Geometry.IsEmpty)
            {
                index.Insert(feature.Geometry.EnvelopeInternal, feature);
            }
        }

        index.Build();
        return index;
    }

    private static Stream OpenReferenceStream(TransformConfig config)
    {
        if (config.Options.TryGetValue("referenceInline", out var inline) && !string.IsNullOrEmpty(inline))
        {
            return new MemoryStream(Encoding.UTF8.GetBytes(inline), writable: false);
        }

        if (config.Options.TryGetValue("referencePath", out var path) && !string.IsNullOrWhiteSpace(path))
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 65536,
                useAsync: true);
        }

        throw new InvalidOperationException(
            "Spatial-join transform requires a 'referenceInline' GeoJSON document or a 'referencePath' option.");
    }
}
