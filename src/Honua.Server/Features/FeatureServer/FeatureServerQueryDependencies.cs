// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Validation.Abstractions;
using Honua.Core.Queries.Filters;
using Honua.Server.Features.FeatureServer.Services;

namespace Honua.Server.Features.FeatureServer;

internal sealed class FeatureServerQueryDependencies
{
    public FeatureServerQueryDependencies(
        IResourceValidator resourceValidator,
        IFeatureServerQueryServices queryServices,
        IFilterExpressionService filterExpressionService,
        FeatureServerQueryExecutor queryExecutor)
    {
        ResourceValidator = resourceValidator ?? throw new ArgumentNullException(nameof(resourceValidator));
        QueryServices = queryServices ?? throw new ArgumentNullException(nameof(queryServices));
        FilterExpressionService = filterExpressionService ?? throw new ArgumentNullException(nameof(filterExpressionService));
        QueryExecutor = queryExecutor ?? throw new ArgumentNullException(nameof(queryExecutor));
    }

    public IResourceValidator ResourceValidator { get; }
    public IFeatureServerQueryServices QueryServices { get; }
    public IFilterExpressionService FilterExpressionService { get; }
    public FeatureServerQueryExecutor QueryExecutor { get; }
}
