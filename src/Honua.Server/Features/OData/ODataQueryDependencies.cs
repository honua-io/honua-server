// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Caching;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Caching;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Server.Features.OData.Services;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.OData;

internal sealed class ODataQueryDependencies
{
    public ODataQueryDependencies(
        ILayerCatalog layerCatalog,
        IResourceValidator resourceValidator,
        IFeatureReader featureReader,
        IGeometryService geometryService,
        ICrsRegistry crsRegistry,
        ODataValidationService validationService,
        ODataQuerySearchService querySearchService,
        IResponseCache responseCache,
        IOptions<CacheOptions> cacheOptions)
    {
        LayerCatalog = layerCatalog ?? throw new ArgumentNullException(nameof(layerCatalog));
        ResourceValidator = resourceValidator ?? throw new ArgumentNullException(nameof(resourceValidator));
        FeatureReader = featureReader ?? throw new ArgumentNullException(nameof(featureReader));
        GeometryService = geometryService ?? throw new ArgumentNullException(nameof(geometryService));
        CrsRegistry = crsRegistry ?? throw new ArgumentNullException(nameof(crsRegistry));
        ValidationService = validationService ?? throw new ArgumentNullException(nameof(validationService));
        QuerySearchService = querySearchService ?? throw new ArgumentNullException(nameof(querySearchService));
        ResponseCache = responseCache ?? throw new ArgumentNullException(nameof(responseCache));
        CacheOptions = cacheOptions?.Value ?? throw new ArgumentNullException(nameof(cacheOptions));
    }

    public ILayerCatalog LayerCatalog { get; }
    public IResourceValidator ResourceValidator { get; }
    public IFeatureReader FeatureReader { get; }
    public IGeometryService GeometryService { get; }
    public ICrsRegistry CrsRegistry { get; }
    public ODataValidationService ValidationService { get; }
    public ODataQuerySearchService QuerySearchService { get; }
    public IResponseCache ResponseCache { get; }
    public CacheOptions CacheOptions { get; }
}
