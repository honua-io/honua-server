// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Caching;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Caching;
using Honua.Infrastructure.Caching;
using Honua.Protocols.OData.Services;
using Microsoft.Extensions.Options;

namespace Honua.Protocols.OData;

internal sealed class ODataQueryDependencies
{
    public ODataQueryDependencies(
        IFeatureReader featureReader,
        IGeometryService geometryService,
        ICrsRegistry crsRegistry,
        ODataValidationService validationService,
        ODataQuerySearchService querySearchService,
        IResponseCache responseCache,
        IETagService etagService,
        IOptions<CacheOptions> cacheOptions)
    {
        FeatureReader = featureReader ?? throw new ArgumentNullException(nameof(featureReader));
        GeometryService = geometryService ?? throw new ArgumentNullException(nameof(geometryService));
        CrsRegistry = crsRegistry ?? throw new ArgumentNullException(nameof(crsRegistry));
        ValidationService = validationService ?? throw new ArgumentNullException(nameof(validationService));
        QuerySearchService = querySearchService ?? throw new ArgumentNullException(nameof(querySearchService));
        ResponseCache = responseCache ?? throw new ArgumentNullException(nameof(responseCache));
        ETagService = etagService ?? throw new ArgumentNullException(nameof(etagService));
        CacheOptions = cacheOptions?.Value ?? throw new ArgumentNullException(nameof(cacheOptions));
    }

    public IFeatureReader FeatureReader { get; }
    public IGeometryService GeometryService { get; }
    public ICrsRegistry CrsRegistry { get; }
    public ODataValidationService ValidationService { get; }
    public ODataQuerySearchService QuerySearchService { get; }
    public IResponseCache ResponseCache { get; }
    public IETagService ETagService { get; }
    public CacheOptions CacheOptions { get; }
}
