// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Microsoft.AspNetCore.OutputCaching;

namespace Honua.Server.Features.Infrastructure.Caching;

/// <summary>
/// Output cache policy that adds tags derived from common route values.
/// </summary>
internal sealed class RouteTagOutputCachePolicy : IOutputCachePolicy
{
    public ValueTask CacheRequestAsync(OutputCacheContext context, CancellationToken cancellationToken)
    {
        AddRouteTags(context);
        return ValueTask.CompletedTask;
    }

    public ValueTask ServeFromCacheAsync(OutputCacheContext context, CancellationToken cancellationToken)
    {
        return ValueTask.CompletedTask;
    }

    public ValueTask ServeResponseAsync(OutputCacheContext context, CancellationToken cancellationToken)
    {
        AddRouteTags(context);
        return ValueTask.CompletedTask;
    }

    private static void AddRouteTags(OutputCacheContext context)
    {
        var routeValues = context.HttpContext.Request.RouteValues;

        AddTag(context, routeValues, "serviceId", "service");
        AddTag(context, routeValues, "layerId", "layer");
        AddTag(context, routeValues, "collectionId", "collection");
    }

    private static void AddTag(OutputCacheContext context, RouteValueDictionary routeValues, string routeKey, string tagPrefix)
    {
        if (!routeValues.TryGetValue(routeKey, out var value) || value == null)
        {
            return;
        }

        var tagValue = Convert.ToString(value, CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(tagValue))
        {
            return;
        }

        context.Tags.Add($"{tagPrefix}:{tagValue.ToLowerInvariant()}");
    }
}
