// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Validation.Abstractions;
using Honua.Core.Queries.Filters;
using Honua.Protocols.GeoServices.FeatureServer.Services;

namespace Honua.Protocols.GeoServices.FeatureServer;

internal sealed class FeatureServerRelatedRecordsDependencies
{
    public FeatureServerRelatedRecordsDependencies(
        IResourceValidator resourceValidator,
        IFeatureServerQueryServices queryServices,
        IRelatedRecordsService relatedRecordsService,
        IFilterExpressionService filterExpressionService,
        IHttpContextAccessor httpContextAccessor)
    {
        ResourceValidator = resourceValidator ?? throw new ArgumentNullException(nameof(resourceValidator));
        QueryServices = queryServices ?? throw new ArgumentNullException(nameof(queryServices));
        RelatedRecordsService = relatedRecordsService ?? throw new ArgumentNullException(nameof(relatedRecordsService));
        FilterExpressionService = filterExpressionService ?? throw new ArgumentNullException(nameof(filterExpressionService));
        HttpContextAccessor = httpContextAccessor ?? throw new ArgumentNullException(nameof(httpContextAccessor));
    }

    public IResourceValidator ResourceValidator { get; }
    public IFeatureServerQueryServices QueryServices { get; }
    public IRelatedRecordsService RelatedRecordsService { get; }
    public IFilterExpressionService FilterExpressionService { get; }
    public IHttpContextAccessor HttpContextAccessor { get; }
}
