// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Caching;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Caching;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Server.Features.Infrastructure.Caching;
using Honua.Server.Features.OgcFeatures.Services;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.OgcFeatures;

internal sealed class OgcFeaturesQueryDependencies
{
    public OgcFeaturesQueryDependencies(
        IFeatureReader featureReader,
        IStreamingFeatureStore streamingFeatureStore,
        IResourceValidator resourceValidator,
        ICommonQueryValidator queryValidator,
        ICrsRegistry crsRegistry,
        OgcFilterProcessor filterProcessor,
        OgcFeaturesGeometryServices geometryServices,
        IResponseCache responseCache,
        IETagService etagService,
        IOptions<CacheOptions> cacheOptions,
        IOptions<OgcFeaturesOptions> ogcFeaturesOptions)
    {
        FeatureReader = featureReader ?? throw new ArgumentNullException(nameof(featureReader));
        StreamingFeatureStore = streamingFeatureStore ?? throw new ArgumentNullException(nameof(streamingFeatureStore));
        ResourceValidator = resourceValidator ?? throw new ArgumentNullException(nameof(resourceValidator));
        QueryValidator = queryValidator ?? throw new ArgumentNullException(nameof(queryValidator));
        CrsRegistry = crsRegistry ?? throw new ArgumentNullException(nameof(crsRegistry));
        FilterProcessor = filterProcessor ?? throw new ArgumentNullException(nameof(filterProcessor));
        GeometryServices = geometryServices ?? throw new ArgumentNullException(nameof(geometryServices));
        ResponseCache = responseCache ?? throw new ArgumentNullException(nameof(responseCache));
        ETagService = etagService ?? throw new ArgumentNullException(nameof(etagService));
        CacheOptions = cacheOptions?.Value ?? throw new ArgumentNullException(nameof(cacheOptions));
        OgcFeaturesOptions = ogcFeaturesOptions?.Value ?? throw new ArgumentNullException(nameof(ogcFeaturesOptions));
    }

    public IFeatureReader FeatureReader { get; }
    public IStreamingFeatureStore StreamingFeatureStore { get; }
    public IResourceValidator ResourceValidator { get; }
    public ICommonQueryValidator QueryValidator { get; }
    public ICrsRegistry CrsRegistry { get; }
    public OgcFilterProcessor FilterProcessor { get; }
    public OgcFeaturesGeometryServices GeometryServices { get; }
    public IResponseCache ResponseCache { get; }
    public IETagService ETagService { get; }
    public CacheOptions CacheOptions { get; }
    public OgcFeaturesOptions OgcFeaturesOptions { get; }
}
