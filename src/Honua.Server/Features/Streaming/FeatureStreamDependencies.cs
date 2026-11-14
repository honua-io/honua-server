// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.GeometryService.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Queries.Filters;
using Honua.Infrastructure.Events;
using Honua.Server.Features.Realtime;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Streaming;

internal sealed class FeatureStreamDependencies
{
    public FeatureStreamDependencies(
        FeatureStreamSessionManager sessionManager,
        IFeatureChangeEventStore eventStore,
        IOptions<FeatureStreamOptions> options,
        IOptions<RealtimeOptions> realtimeOptions,
        IFilterExpressionService filterExpressionService,
        IMetadataV2GraphProvider metadataV2GraphProvider,
        FeatureStreamRoutabilityGuard routabilityGuard,
        IGeometryOperationService geometryOperationService,
        IFeatureReader featureReader)
    {
        SessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
        EventStore = eventStore ?? throw new ArgumentNullException(nameof(eventStore));
        Options = options ?? throw new ArgumentNullException(nameof(options));
        RealtimeOptions = realtimeOptions ?? throw new ArgumentNullException(nameof(realtimeOptions));
        FilterExpressionService = filterExpressionService ?? throw new ArgumentNullException(nameof(filterExpressionService));
        MetadataV2GraphProvider = metadataV2GraphProvider ?? throw new ArgumentNullException(nameof(metadataV2GraphProvider));
        RoutabilityGuard = routabilityGuard ?? throw new ArgumentNullException(nameof(routabilityGuard));
        GeometryOperationService = geometryOperationService ?? throw new ArgumentNullException(nameof(geometryOperationService));
        FeatureReader = featureReader ?? throw new ArgumentNullException(nameof(featureReader));
    }

    public FeatureStreamSessionManager SessionManager { get; }
    public IFeatureChangeEventStore EventStore { get; }
    public IOptions<FeatureStreamOptions> Options { get; }
    public IOptions<RealtimeOptions> RealtimeOptions { get; }
    public IFilterExpressionService FilterExpressionService { get; }
    public IMetadataV2GraphProvider MetadataV2GraphProvider { get; }
    public FeatureStreamRoutabilityGuard RoutabilityGuard { get; }
    public IGeometryOperationService GeometryOperationService { get; }

    public IFeatureReader FeatureReader { get; }
}
