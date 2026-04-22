// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Edit;
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
        IFeatureReader featureReader,
        IFeatureServerGeometryServices geometryServices,
        IEditParameterAdapter<GeoServicesEditRequest> editParameterAdapter,
        IEditProcessor editProcessor,
        FeatureMutationValidator mutationValidator,
        IHttpContextAccessor httpContextAccessor,
        FeatureMutationEventService mutationEventService)
    {
        ResourceValidator = resourceValidator ?? throw new ArgumentNullException(nameof(resourceValidator));
        FeatureWriter = featureWriter ?? throw new ArgumentNullException(nameof(featureWriter));
        FeatureReader = featureReader ?? throw new ArgumentNullException(nameof(featureReader));
        GeometryServices = geometryServices ?? throw new ArgumentNullException(nameof(geometryServices));
        EditParameterAdapter = editParameterAdapter ?? throw new ArgumentNullException(nameof(editParameterAdapter));
        EditProcessor = editProcessor ?? throw new ArgumentNullException(nameof(editProcessor));
        MutationValidator = mutationValidator ?? throw new ArgumentNullException(nameof(mutationValidator));
        HttpContextAccessor = httpContextAccessor ?? throw new ArgumentNullException(nameof(httpContextAccessor));
        MutationEventService = mutationEventService ?? throw new ArgumentNullException(nameof(mutationEventService));
    }

    public IResourceValidator ResourceValidator { get; }
    public IFeatureWriter FeatureWriter { get; }
    public IFeatureReader FeatureReader { get; }
    public IFeatureServerGeometryServices GeometryServices { get; }
    public IEditParameterAdapter<GeoServicesEditRequest> EditParameterAdapter { get; }
    public IEditProcessor EditProcessor { get; }
    public FeatureMutationValidator MutationValidator { get; }
    public IHttpContextAccessor HttpContextAccessor { get; }
    public FeatureMutationEventService MutationEventService { get; }
}
