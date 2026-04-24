// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Edit;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Query;
using Honua.Server.Features.Infrastructure.Events;
using Honua.Server.Features.Infrastructure.Caching;
using Honua.Server.Features.Infrastructure.Validation;
using Honua.Server.Features.OgcFeatures.Services;

namespace Honua.Server.Features.OgcFeatures;

internal sealed class OgcFeaturesCrudDependencies
{
    public OgcFeaturesCrudDependencies(
        IFeatureReader featureReader,
        IFeatureWriter featureWriter,
        ICrsRegistry crsRegistry,
        OgcFeaturesGeometryServices geometryServices,
        IEditParameterAdapter<OgcFeaturesEditRequest> editParameterAdapter,
        IEditProcessor editProcessor,
        IQueryProcessor queryProcessor,
        IETagService etagService,
        FeatureMutationValidator mutationValidator,
        FeatureMutationEventService mutationEventService)
    {
        FeatureReader = featureReader ?? throw new ArgumentNullException(nameof(featureReader));
        FeatureWriter = featureWriter ?? throw new ArgumentNullException(nameof(featureWriter));
        CrsRegistry = crsRegistry ?? throw new ArgumentNullException(nameof(crsRegistry));
        GeometryServices = geometryServices ?? throw new ArgumentNullException(nameof(geometryServices));
        EditParameterAdapter = editParameterAdapter ?? throw new ArgumentNullException(nameof(editParameterAdapter));
        EditProcessor = editProcessor ?? throw new ArgumentNullException(nameof(editProcessor));
        QueryProcessor = queryProcessor ?? throw new ArgumentNullException(nameof(queryProcessor));
        ETagService = etagService ?? throw new ArgumentNullException(nameof(etagService));
        MutationValidator = mutationValidator ?? throw new ArgumentNullException(nameof(mutationValidator));
        MutationEventService = mutationEventService ?? throw new ArgumentNullException(nameof(mutationEventService));
    }

    public IFeatureReader FeatureReader { get; }
    public IFeatureWriter FeatureWriter { get; }
    public ICrsRegistry CrsRegistry { get; }
    public OgcFeaturesGeometryServices GeometryServices { get; }
    public IEditParameterAdapter<OgcFeaturesEditRequest> EditParameterAdapter { get; }
    public IEditProcessor EditProcessor { get; }
    public IQueryProcessor QueryProcessor { get; }
    public IETagService ETagService { get; }
    public FeatureMutationValidator MutationValidator { get; }
    public FeatureMutationEventService MutationEventService { get; }
}
