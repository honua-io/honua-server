// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Server.Features.Infrastructure.Filtering;

namespace Honua.Server.Features.Stac;

internal sealed class StacSearchDependencies
{
    public StacSearchDependencies(
        ILayerCatalog layerCatalog,
        IFeatureReader featureReader,
        IGeometryService geometryService,
        Cql2FilterProcessor filterProcessor,
        ILogger<StacEndpoints.StacEndpointsLog> logger)
    {
        LayerCatalog = layerCatalog ?? throw new ArgumentNullException(nameof(layerCatalog));
        FeatureReader = featureReader ?? throw new ArgumentNullException(nameof(featureReader));
        GeometryService = geometryService ?? throw new ArgumentNullException(nameof(geometryService));
        FilterProcessor = filterProcessor ?? throw new ArgumentNullException(nameof(filterProcessor));
        Logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public ILayerCatalog LayerCatalog { get; }
    public IFeatureReader FeatureReader { get; }
    public IGeometryService GeometryService { get; }
    public Cql2FilterProcessor FilterProcessor { get; }
    public ILogger<StacEndpoints.StacEndpointsLog> Logger { get; }
}
