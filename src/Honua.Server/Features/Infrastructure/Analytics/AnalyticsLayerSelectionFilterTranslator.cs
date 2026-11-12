// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;

namespace Honua.Infrastructure.Analytics;

internal sealed class AnalyticsLayerSelectionFilterTranslator : ILayerSelectionFilterTranslator
{
    private readonly IServiceProvider _services;

    public AnalyticsLayerSelectionFilterTranslator(IServiceProvider services)
    {
        _services = services;
    }

    public Task<LayerSelectionTranslation> TranslateAsync(
        LayerSelectionFilter selection,
        MetadataV2Object resource,
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
