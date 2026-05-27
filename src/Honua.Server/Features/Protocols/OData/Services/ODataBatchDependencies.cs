// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Configuration;
using Honua.Core.Features.Edit;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Validation;
using Honua.Server.Features.Infrastructure.Caching;
using Honua.Server.Features.Infrastructure.Events;
using Honua.Server.Features.Infrastructure.Validation;
using Honua.Server.Features.Protocols.OData;

namespace Honua.Server.Features.Protocols.OData.Services;

/// <summary>
/// Aggregates OData batch dependencies to keep handler constructor limits small.
/// </summary>
internal sealed class ODataBatchDependencies
{
    public ODataBatchDependencies(
        IFeatureReader featureReader,
        IFeatureWriter featureWriter,
        IGeometryService geometryService,
        FeatureMutationValidator mutationValidator,
        ICrsRegistry crsRegistry,
        EditLimits editLimits,
        ODataValidationService validationService,
        IETagService eTagService,
        IEditParameterAdapter<ODataEditRequest> editParameterAdapter,
        IEditProcessor editProcessor,
        IFeatureChangeEventPublisher featureChangeEventPublisher)
        : this(
            featureReader,
            featureWriter,
            geometryService,
            mutationValidator,
            crsRegistry,
            editLimits,
            validationService,
            eTagService,
            editParameterAdapter,
            editProcessor,
            new FeatureMutationEventService(featureChangeEventPublisher))
    {
    }

    public ODataBatchDependencies(
        IFeatureReader featureReader,
        IFeatureWriter featureWriter,
        IGeometryService geometryService,
        FeatureMutationValidator mutationValidator,
        ICrsRegistry crsRegistry,
        EditLimits editLimits,
        ODataValidationService validationService,
        IETagService eTagService,
        IEditParameterAdapter<ODataEditRequest> editParameterAdapter,
        IEditProcessor editProcessor,
        FeatureMutationEventService mutationEventService)
    {
        // Validation framework eliminates 10 lines of duplicate null checks
        FeatureReader = featureReader.ThrowIfNull();
        FeatureWriter = featureWriter.ThrowIfNull();
        GeometryService = geometryService.ThrowIfNull();
        MutationValidator = mutationValidator.ThrowIfNull();
        CrsRegistry = crsRegistry.ThrowIfNull();
        EditLimits = editLimits.ThrowIfNull();
        ValidationService = validationService.ThrowIfNull();
        ETagService = eTagService.ThrowIfNull();
        EditParameterAdapter = editParameterAdapter.ThrowIfNull();
        EditProcessor = editProcessor.ThrowIfNull();
        MutationEventService = mutationEventService.ThrowIfNull();
    }

    public IFeatureReader FeatureReader { get; }

    public IFeatureWriter FeatureWriter { get; }

    public IGeometryService GeometryService { get; }

    public FeatureMutationValidator MutationValidator { get; }

    public ICrsRegistry CrsRegistry { get; }

    public EditLimits EditLimits { get; }

    public ODataValidationService ValidationService { get; }

    public IETagService ETagService { get; }

    public IEditParameterAdapter<ODataEditRequest> EditParameterAdapter { get; }

    public IEditProcessor EditProcessor { get; }

    public FeatureMutationEventService MutationEventService { get; }
}
