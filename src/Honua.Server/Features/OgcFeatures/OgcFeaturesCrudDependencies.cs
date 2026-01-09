// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Server.Features.OgcFeatures.Services;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.OgcFeatures;

internal sealed class OgcFeaturesCrudDependencies
{
    public OgcFeaturesCrudDependencies(
        IFeatureWriter featureWriter,
        IResourceValidator resourceValidator,
        IOptions<LimitsOptions> limitsOptions,
        OgcFeaturesGeometryServices geometryServices)
    {
        FeatureWriter = featureWriter ?? throw new ArgumentNullException(nameof(featureWriter));
        ResourceValidator = resourceValidator ?? throw new ArgumentNullException(nameof(resourceValidator));
        GeometryServices = geometryServices ?? throw new ArgumentNullException(nameof(geometryServices));
        LimitsOptions = limitsOptions?.Value ?? throw new ArgumentNullException(nameof(limitsOptions));
    }

    public IFeatureWriter FeatureWriter { get; }
    public IResourceValidator ResourceValidator { get; }
    public LimitsOptions LimitsOptions { get; }
    public OgcFeaturesGeometryServices GeometryServices { get; }
}
