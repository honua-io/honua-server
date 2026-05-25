// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Runtime.CompilerServices;
using System.Text;
using Honua.Core.Features.GeoETL.Abstractions;
using Honua.Core.Features.Import.Services;
using NetTopologySuite.Features;

namespace Honua.Core.Features.GeoETL.Services.Connectors;

/// <summary>
/// Managed <see cref="ILayerFeatureSource"/> for the inline-GeoJSON input case.
/// Wraps the existing <see cref="StreamingGeoJsonReader"/> — it does not
/// reimplement GeoJSON parsing — so a layer-scope GP process can accept an inline
/// FeatureCollection without any storage provider. The reader streams features one
/// at a time so the source keeps constant memory.
/// </summary>
/// <remarks>
/// The catalog-layer-id and query-result reference kinds are resolved by a storage
/// provider implementation (Honua.Postgres); this managed source intentionally
/// handles only <see cref="LayerFeatureSourceKind.InlineGeoJson"/> so the lean
/// serving image can run the inline path with no native or database dependency.
/// </remarks>
public sealed class InlineGeoJsonLayerFeatureSource : ILayerFeatureSource
{
    /// <inheritdoc />
    public bool CanResolve(LayerFeatureSourceKind kind)
        => kind == LayerFeatureSourceKind.InlineGeoJson;

    /// <inheritdoc />
    public async IAsyncEnumerable<IFeature> ReadAsync(
        LayerFeatureReference reference,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);

        if (reference.Kind != LayerFeatureSourceKind.InlineGeoJson)
        {
            throw new InvalidOperationException(
                $"InlineGeoJsonLayerFeatureSource cannot resolve reference kind '{reference.Kind}'. " +
                "Catalog-layer and query-result references require a storage-provider source.");
        }

        if (string.IsNullOrWhiteSpace(reference.InlineGeoJson))
        {
            throw new InvalidOperationException(
                "Inline-GeoJSON layer reference requires a non-empty FeatureCollection document.");
        }

        var reader = new StreamingGeoJsonReader();
        await using var stream = new MemoryStream(
            Encoding.UTF8.GetBytes(reference.InlineGeoJson), writable: false);

        await foreach (var feature in reader.ReadFeaturesAsync(stream, cancellationToken).ConfigureAwait(false))
        {
            yield return feature;
        }
    }
}
