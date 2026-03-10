// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Queries.Filters;
using Honua.Server.Features.OgcFeatures;
using Honua.Server.Features.OgcFeatures.Services;

namespace Honua.Server.Features.Wfs20.Services;

/// <summary>
/// Feature-local facade that groups the shared query services used by the WFS handler.
/// Keeps the handler constructor within the architecture test limit without changing behavior.
/// </summary>
internal sealed class Wfs20QueryServices(
    ILayerCatalog layerCatalog,
    IFeatureReader featureReader,
    IGmlFeatureStore gmlFeatureStore,
    IFilterExpressionService filterExpressionService,
    OgcFeaturesGeometryServices geometryServices)
{
    public ILayerCatalog LayerCatalog { get; } = layerCatalog;

    public IFeatureReader FeatureReader { get; } = featureReader;

    public IGmlFeatureStore GmlFeatureStore { get; } = gmlFeatureStore;

    public IFilterExpressionService FilterExpressionService { get; } = filterExpressionService;

    public OgcFeaturesGeometryServices GeometryServices { get; } = geometryServices;
}
