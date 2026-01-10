// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Server.Features.OgcFeatures.Services;

namespace Honua.Server.Features.OgcFeatures;

internal sealed class OgcFeaturesTransactionDependencies
{
    public OgcFeaturesTransactionDependencies(
        IFeatureReader featureReader,
        IFeatureWriter featureWriter,
        IResourceValidator resourceValidator,
        OgcFeaturesGeometryServices geometryServices,
        IGeometryValidator geometryValidator)
    {
        FeatureReader = featureReader ?? throw new ArgumentNullException(nameof(featureReader));
        FeatureWriter = featureWriter ?? throw new ArgumentNullException(nameof(featureWriter));
        ResourceValidator = resourceValidator ?? throw new ArgumentNullException(nameof(resourceValidator));
        GeometryServices = geometryServices ?? throw new ArgumentNullException(nameof(geometryServices));
        GeometryValidator = geometryValidator ?? throw new ArgumentNullException(nameof(geometryValidator));
    }

    public IFeatureReader FeatureReader { get; }
    public IFeatureWriter FeatureWriter { get; }
    public IResourceValidator ResourceValidator { get; }
    public OgcFeaturesGeometryServices GeometryServices { get; }
    public IGeometryValidator GeometryValidator { get; }
}
