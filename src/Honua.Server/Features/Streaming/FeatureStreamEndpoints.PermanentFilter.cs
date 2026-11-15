// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Queries.Filters;

namespace Honua.Server.Features.Streaming;

internal static partial class FeatureStreamEndpoints
{
    private static bool PermanentFilterAccepted(
        MetadataV2Object resource,
        IFilterExpressionService filterExpressionService,
        List<FilterExpression> predicates)
    {
        try
        {
            AppendPermanentFilterPredicate(resource, filterExpressionService, predicates);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    internal static void AppendPermanentFilterPredicate(
        MetadataV2Object resource,
        IFilterExpressionService filterExpressionService,
        List<FilterExpression> predicates)
    {
        ArgumentNullException.ThrowIfNull(predicates);
        var expression = Honua.Core.Features.FeatureStore.Services.LayerReadSecurityResolver
            .RequirePermanentFilterExpression(resource, filterExpressionService);
        if (expression is null)
        {
            return;
        }

        // The same depth guard as a subscriber filter. Past the limit the
        // evaluator fails closed, and a surrounding NOT would invert that.
        if (InMemoryFilterEvaluator.ExceedsMaxDepth(expression))
        {
            throw new InvalidOperationException(
                $"Saved permanent filter for resource '{resource.Metadata.Id}' exceeds the streaming depth limit.");
        }

        if (!InMemoryFilterEvaluator.TryValidateStreamingExpression(expression, out var error))
        {
            throw new InvalidOperationException(error ?? "Saved permanent filter cannot be evaluated.");
        }

        predicates.Add(expression);
    }
}
