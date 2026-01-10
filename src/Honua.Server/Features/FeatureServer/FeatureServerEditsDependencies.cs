// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Server.Features.FeatureServer.Services;
namespace Honua.Server.Features.FeatureServer;

internal sealed class FeatureServerEditsDependencies
{
    public FeatureServerEditsDependencies(
        IResourceValidator resourceValidator,
        IFeatureWriter featureWriter,
        IFeatureServerGeometryServices geometryServices,
        IHttpContextAccessor httpContextAccessor)
    {
        ResourceValidator = resourceValidator ?? throw new ArgumentNullException(nameof(resourceValidator));
        FeatureWriter = featureWriter ?? throw new ArgumentNullException(nameof(featureWriter));
        GeometryServices = geometryServices ?? throw new ArgumentNullException(nameof(geometryServices));
        HttpContextAccessor = httpContextAccessor ?? throw new ArgumentNullException(nameof(httpContextAccessor));
    }

    public IResourceValidator ResourceValidator { get; }
    public IFeatureWriter FeatureWriter { get; }
    public IFeatureServerGeometryServices GeometryServices { get; }
    public IHttpContextAccessor HttpContextAccessor { get; }
}
