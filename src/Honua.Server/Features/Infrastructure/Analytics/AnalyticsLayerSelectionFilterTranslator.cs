// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;

namespace Honua.Infrastructure.Analytics;

/// <summary>
/// The <see cref="ILayerSelectionFilterTranslator"/> used by the <c>source.honua-layer</c>
/// geoprocessing connector. It delegates to <see cref="AnalyticsFeatureQueryFactory.TranslateAsync"/>,
/// the same translation the synchronous spatial analytics endpoints use, so a layer-sourced
/// job and its synchronous sibling select exactly the same rows for the same selectors (#4624).
/// Registered scoped so the filter service and spatial-reference resolver come from the
/// caller's scope.
/// </summary>
internal sealed class AnalyticsLayerSelectionFilterTranslator : ILayerSelectionFilterTranslator
{
    private readonly IServiceProvider _services;

    public AnalyticsLayerSelectionFilterTranslator(IServiceProvider services)
    {
        _services = services;
    }

    /// <inheritdoc />
    public Task<LayerSelectionTranslation> TranslateAsync(
        LayerSelectionFilter selection,
        MetadataV2Resource resource,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resource);
        return AnalyticsFeatureQueryFactory.TranslateAsync(
            selection,
            resource,
            resource.ReadSrid() ?? SpatialReference.WGS84.ToSrid(),
            _services,
            cancellationToken);
    }
}
