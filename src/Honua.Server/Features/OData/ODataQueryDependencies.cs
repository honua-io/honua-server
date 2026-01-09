// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Server.Features.OData.Services;

namespace Honua.Server.Features.OData;

internal sealed class ODataQueryDependencies
{
    public ODataQueryDependencies(
        ILayerCatalog layerCatalog,
        IResourceValidator resourceValidator,
        IFeatureReader featureReader,
        ODataValidationService validationService,
        ODataQuerySearchService querySearchService)
    {
        LayerCatalog = layerCatalog ?? throw new ArgumentNullException(nameof(layerCatalog));
        ResourceValidator = resourceValidator ?? throw new ArgumentNullException(nameof(resourceValidator));
        FeatureReader = featureReader ?? throw new ArgumentNullException(nameof(featureReader));
        ValidationService = validationService ?? throw new ArgumentNullException(nameof(validationService));
        QuerySearchService = querySearchService ?? throw new ArgumentNullException(nameof(querySearchService));
    }

    public ILayerCatalog LayerCatalog { get; }
    public IResourceValidator ResourceValidator { get; }
    public IFeatureReader FeatureReader { get; }
    public ODataValidationService ValidationService { get; }
    public ODataQuerySearchService QuerySearchService { get; }
}
