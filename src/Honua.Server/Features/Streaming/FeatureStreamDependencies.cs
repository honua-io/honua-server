// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.GeometryService.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Queries.Filters;
using Honua.Infrastructure.Events;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Streaming;

/// <summary>
/// Dependency bundle for feature stream endpoints. Keeps the endpoint method
/// signature within the 5-dependency limit (follows <c>FeatureServerEditsDependencies</c> pattern).
/// </summary>
internal sealed class FeatureStreamDependencies
{
    public FeatureStreamDependencies(
        FeatureStreamSessionManager sessionManager,
        IFeatureChangeEventStore eventStore,
        IOptions<FeatureStreamOptions> options,
        IOptions<FeatureChangeEventOptions> eventOptions,
        IFilterExpressionService filterExpressionService,
        IMetadataV2GraphProvider metadataV2GraphProvider,
        IGeometryOperationService geometryOperationService)
    {
        SessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
        EventStore = eventStore ?? throw new ArgumentNullException(nameof(eventStore));
        Options = options ?? throw new ArgumentNullException(nameof(options));
        EventOptions = eventOptions ?? throw new ArgumentNullException(nameof(eventOptions));
        FilterExpressionService = filterExpressionService ?? throw new ArgumentNullException(nameof(filterExpressionService));
        MetadataV2GraphProvider = metadataV2GraphProvider ?? throw new ArgumentNullException(nameof(metadataV2GraphProvider));
        GeometryOperationService = geometryOperationService ?? throw new ArgumentNullException(nameof(geometryOperationService));
    }

    public FeatureStreamSessionManager SessionManager { get; }
    public IFeatureChangeEventStore EventStore { get; }
    public IOptions<FeatureStreamOptions> Options { get; }
    public IOptions<FeatureChangeEventOptions> EventOptions { get; }
    public IFilterExpressionService FilterExpressionService { get; }
    public IMetadataV2GraphProvider MetadataV2GraphProvider { get; }
    public IGeometryOperationService GeometryOperationService { get; }
}
