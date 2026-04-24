// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Server.Features.FeatureServer;
using Honua.Server.Features.Infrastructure.Helpers;
using Microsoft.Extensions.Primitives;

namespace Honua.Server.Features.MapServer;

internal static partial class MapServerEndpoints
{
    private static async Task<(Dictionary<string, StringValues>? Values, string? Error)> TryReadMapServerRequestValuesAsync(HttpContext context)
    {
        var cancellationToken = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        var values = FeatureServerEndpoints.ToCaseInsensitiveDictionary(context.Request.Query);

        if (!HttpMethods.IsPost(context.Request.Method) && !HttpMethods.IsPut(context.Request.Method))
        {
            return (values, null);
        }

        if (!context.Request.HasFormContentType && (context.Request.ContentLength is null or 0))
        {
            return (values, null);
        }

        var (bodyValues, error) = await FeatureServerEndpoints.TryReadRequestValuesAsync(
            context.Request,
            cancellationToken);
        if (bodyValues == null)
        {
            return (null, error ?? "Invalid request body.");
        }

        foreach (var kvp in bodyValues)
        {
            values[kvp.Key] = kvp.Value;
        }

        return (values, null);
    }

    private static string? GetValue(Dictionary<string, StringValues> values, string key)
        => values.TryGetValue(key, out var raw) ? raw.ToString() : null;
}
