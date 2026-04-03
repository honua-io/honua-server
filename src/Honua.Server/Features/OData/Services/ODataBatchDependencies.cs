// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Configuration;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Server.Features.Infrastructure.Caching;
using Honua.Server.Features.Infrastructure.Events;
using Honua.Server.Features.Infrastructure.Validation;
using Honua.Server.Features.OData;

namespace Honua.Server.Features.OData.Services;

/// <summary>
/// Aggregates OData batch dependencies to keep handler constructor limits small.
/// </summary>
internal sealed class ODataBatchDependencies
{
    public ODataBatchDependencies(
        ILayerCatalog layerCatalog,
        IFeatureReader featureReader,
        IFeatureWriter featureWriter,
        IGeometryService geometryService,
        FeatureMutationValidator mutationValidator,
        ICrsRegistry crsRegistry,
        EditLimits editLimits,
        ODataValidationService validationService,
        IETagService eTagService,
        IFeatureChangeEventPublisher featureChangeEventPublisher)
        : this(
            layerCatalog,
            featureReader,
            featureWriter,
            geometryService,
            mutationValidator,
            crsRegistry,
            editLimits,
            validationService,
            eTagService,
            new FeatureMutationEventService(featureChangeEventPublisher))
    {
    }

    public ODataBatchDependencies(
        ILayerCatalog layerCatalog,
        IFeatureReader featureReader,
        IFeatureWriter featureWriter,
        IGeometryService geometryService,
        FeatureMutationValidator mutationValidator,
        ICrsRegistry crsRegistry,
        EditLimits editLimits,
        ODataValidationService validationService,
        IETagService eTagService,
        FeatureMutationEventService mutationEventService)
    {
        LayerCatalog = layerCatalog ?? throw new ArgumentNullException(nameof(layerCatalog));
        FeatureReader = featureReader ?? throw new ArgumentNullException(nameof(featureReader));
        FeatureWriter = featureWriter ?? throw new ArgumentNullException(nameof(featureWriter));
        GeometryService = geometryService ?? throw new ArgumentNullException(nameof(geometryService));
        MutationValidator = mutationValidator ?? throw new ArgumentNullException(nameof(mutationValidator));
        CrsRegistry = crsRegistry ?? throw new ArgumentNullException(nameof(crsRegistry));
        EditLimits = editLimits ?? throw new ArgumentNullException(nameof(editLimits));
        ValidationService = validationService ?? throw new ArgumentNullException(nameof(validationService));
        ETagService = eTagService ?? throw new ArgumentNullException(nameof(eTagService));
        MutationEventService = mutationEventService ?? throw new ArgumentNullException(nameof(mutationEventService));
    }

    public ILayerCatalog LayerCatalog { get; }

    public IFeatureReader FeatureReader { get; }

    public IFeatureWriter FeatureWriter { get; }

    public IGeometryService GeometryService { get; }

    public FeatureMutationValidator MutationValidator { get; }

    public ICrsRegistry CrsRegistry { get; }

    public EditLimits EditLimits { get; }

    public ODataValidationService ValidationService { get; }

    public IETagService ETagService { get; }

    public FeatureMutationEventService MutationEventService { get; }
}
