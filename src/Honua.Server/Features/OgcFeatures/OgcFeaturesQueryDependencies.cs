// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Server.Features.OgcFeatures.Services;

namespace Honua.Server.Features.OgcFeatures;

internal sealed class OgcFeaturesQueryDependencies
{
    public OgcFeaturesQueryDependencies(
        IFeatureReader featureReader,
        IStreamingFeatureStore streamingFeatureStore,
        IResourceValidator resourceValidator,
        ICommonQueryValidator queryValidator,
        OgcFilterProcessor filterProcessor)
    {
        FeatureReader = featureReader ?? throw new ArgumentNullException(nameof(featureReader));
        StreamingFeatureStore = streamingFeatureStore ?? throw new ArgumentNullException(nameof(streamingFeatureStore));
        ResourceValidator = resourceValidator ?? throw new ArgumentNullException(nameof(resourceValidator));
        QueryValidator = queryValidator ?? throw new ArgumentNullException(nameof(queryValidator));
        FilterProcessor = filterProcessor ?? throw new ArgumentNullException(nameof(filterProcessor));
    }

    public IFeatureReader FeatureReader { get; }
    public IStreamingFeatureStore StreamingFeatureStore { get; }
    public IResourceValidator ResourceValidator { get; }
    public ICommonQueryValidator QueryValidator { get; }
    public OgcFilterProcessor FilterProcessor { get; }
}
