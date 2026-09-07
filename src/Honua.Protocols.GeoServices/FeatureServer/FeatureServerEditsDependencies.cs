// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Collaboration.FeatureLocks;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Edit;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Core.Queries.Filters;
using Honua.Plugins.Abstractions;
using Honua.Protocols.GeoServices.FeatureServer.Services;
using Honua.Infrastructure.Events;
using Honua.Infrastructure.Validation;

namespace Honua.Protocols.GeoServices.FeatureServer;

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
        IFilterExpressionService filterExpressionService,
        IHttpContextAccessor httpContextAccessor,
        FeatureMutationEventService mutationEventService,
        IPluginEditPipeline pluginPipeline,
        IApplyEditsIdempotencyStore idempotencyStore,
        IFeatureEditGuard editGuard,
        IFeatureLockService featureLocks)
    {
        ResourceValidator = resourceValidator ?? throw new ArgumentNullException(nameof(resourceValidator));
        FeatureWriter = featureWriter ?? throw new ArgumentNullException(nameof(featureWriter));
        FeatureReader = featureReader ?? throw new ArgumentNullException(nameof(featureReader));
        GeometryServices = geometryServices ?? throw new ArgumentNullException(nameof(geometryServices));
        EditParameterAdapter = editParameterAdapter ?? throw new ArgumentNullException(nameof(editParameterAdapter));
        EditProcessor = editProcessor ?? throw new ArgumentNullException(nameof(editProcessor));
        MutationValidator = mutationValidator ?? throw new ArgumentNullException(nameof(mutationValidator));
        FilterExpressionService = filterExpressionService ?? throw new ArgumentNullException(nameof(filterExpressionService));
        HttpContextAccessor = httpContextAccessor ?? throw new ArgumentNullException(nameof(httpContextAccessor));
        MutationEventService = mutationEventService ?? throw new ArgumentNullException(nameof(mutationEventService));
        PluginPipeline = pluginPipeline ?? throw new ArgumentNullException(nameof(pluginPipeline));
        IdempotencyStore = idempotencyStore ?? throw new ArgumentNullException(nameof(idempotencyStore));
        EditGuard = editGuard ?? throw new ArgumentNullException(nameof(editGuard));
        FeatureLocks = featureLocks ?? throw new ArgumentNullException(nameof(featureLocks));
    }

    public IResourceValidator ResourceValidator { get; }
    public IFeatureWriter FeatureWriter { get; }
    public IFeatureReader FeatureReader { get; }
    public IFeatureServerGeometryServices GeometryServices { get; }
    public IEditParameterAdapter<GeoServicesEditRequest> EditParameterAdapter { get; }
    public IEditProcessor EditProcessor { get; }
    public FeatureMutationValidator MutationValidator { get; }
    public IFilterExpressionService FilterExpressionService { get; }
    public IHttpContextAccessor HttpContextAccessor { get; }
    public FeatureMutationEventService MutationEventService { get; }
    public IPluginEditPipeline PluginPipeline { get; }
    public IApplyEditsIdempotencyStore IdempotencyStore { get; }

    /// <summary>
    /// Collaborative-editing guard consulted before an update or delete mutates a
    /// feature, so a lease handed out by <c>/collaboration/feature-locks</c> is
    /// binding on the GeoServices write path (#4402).
    /// </summary>
    public IFeatureEditGuard EditGuard { get; }

    /// <summary>
    /// Lease store, used only for the per-request "is anything locked at all?" probe
    /// that lets an uncontended batch skip per-feature guard evaluation.
    /// </summary>
    public IFeatureLockService FeatureLocks { get; }
}
