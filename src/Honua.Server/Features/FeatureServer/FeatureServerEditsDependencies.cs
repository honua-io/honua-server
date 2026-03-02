// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Server.Features.FeatureServer.Services;
using Honua.Server.Features.Infrastructure.Events;
using Honua.Server.Features.Infrastructure.Validation;

namespace Honua.Server.Features.FeatureServer;

internal sealed class FeatureServerEditsDependencies
{
    public FeatureServerEditsDependencies(
        IResourceValidator resourceValidator,
        IFeatureWriter featureWriter,
        IFeatureServerGeometryServices geometryServices,
        FeatureMutationValidator mutationValidator,
        IHttpContextAccessor httpContextAccessor,
        IFeatureChangeEventPublisher featureChangeEventPublisher)
    {
        ResourceValidator = resourceValidator ?? throw new ArgumentNullException(nameof(resourceValidator));
        FeatureWriter = featureWriter ?? throw new ArgumentNullException(nameof(featureWriter));
        GeometryServices = geometryServices ?? throw new ArgumentNullException(nameof(geometryServices));
        MutationValidator = mutationValidator ?? throw new ArgumentNullException(nameof(mutationValidator));
        HttpContextAccessor = httpContextAccessor ?? throw new ArgumentNullException(nameof(httpContextAccessor));
        FeatureChangeEventPublisher = featureChangeEventPublisher ?? throw new ArgumentNullException(nameof(featureChangeEventPublisher));
    }

    public IResourceValidator ResourceValidator { get; }
    public IFeatureWriter FeatureWriter { get; }
    public IFeatureServerGeometryServices GeometryServices { get; }
    public FeatureMutationValidator MutationValidator { get; }
    public IHttpContextAccessor HttpContextAccessor { get; }
    public IFeatureChangeEventPublisher FeatureChangeEventPublisher { get; }
}
