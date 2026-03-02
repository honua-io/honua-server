// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Server.Features.Infrastructure.Events;
using Honua.Server.Features.Infrastructure.Validation;
using Honua.Server.Features.OgcFeatures.Services;

namespace Honua.Server.Features.OgcFeatures;

internal sealed class OgcFeaturesCrudDependencies
{
    public OgcFeaturesCrudDependencies(
        IFeatureWriter featureWriter,
        ICrsRegistry crsRegistry,
        OgcFeaturesGeometryServices geometryServices,
        FeatureMutationValidator mutationValidator,
        IFeatureChangeEventPublisher featureChangeEventPublisher)
    {
        FeatureWriter = featureWriter ?? throw new ArgumentNullException(nameof(featureWriter));
        CrsRegistry = crsRegistry ?? throw new ArgumentNullException(nameof(crsRegistry));
        GeometryServices = geometryServices ?? throw new ArgumentNullException(nameof(geometryServices));
        MutationValidator = mutationValidator ?? throw new ArgumentNullException(nameof(mutationValidator));
        FeatureChangeEventPublisher = featureChangeEventPublisher ?? throw new ArgumentNullException(nameof(featureChangeEventPublisher));
    }

    public IFeatureWriter FeatureWriter { get; }
    public ICrsRegistry CrsRegistry { get; }
    public OgcFeaturesGeometryServices GeometryServices { get; }
    public FeatureMutationValidator MutationValidator { get; }
    public IFeatureChangeEventPublisher FeatureChangeEventPublisher { get; }
}
