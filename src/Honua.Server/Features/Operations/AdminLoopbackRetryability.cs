// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using Microsoft.Net.Http.Headers;

namespace Honua.Server.Features.Operations;

internal static class AdminLoopbackRetryability
{
    public const string RetryableHeaderName = "Honua-Retryable";

    public static void Apply(
        HttpResponseMessage response,
        HttpContext callerContext,
        IDictionary<string, string> details)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(callerContext);
        ArgumentNullException.ThrowIfNull(details);

        var retryable = ResolveRetryableHeader(response);
        if (retryable is not null)
        {
            details["retryable"] = retryable;
            if (!callerContext.Response.HasStarted)
            {
                callerContext.Response.Headers[RetryableHeaderName] = retryable;
            }
        }

        var retryAfter = ResolveRetryAfterSeconds(response);
        if (retryAfter is not null)
        {
            details["retryAfterSeconds"] = retryAfter;
            if (!callerContext.Response.HasStarted)
            {
                callerContext.Response.Headers[HeaderNames.RetryAfter] = retryAfter;
            }
        }
    }

    private static string? ResolveRetryableHeader(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues(RetryableHeaderName, out var values))
        {
            var value = values.FirstOrDefault(static item => !string.IsNullOrWhiteSpace(item))?.Trim();
            if (!string.IsNullOrEmpty(value))
            {
                return value.ToLowerInvariant();
            }
        }

        return response.StatusCode == HttpStatusCode.TooManyRequests ? "true" : null;
    }

    private static string? ResolveRetryAfterSeconds(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta)
        {
            var seconds = Math.Max(1, (int)Math.Ceiling(delta.TotalSeconds));
            return seconds.ToString(CultureInfo.InvariantCulture);
        }

        if (retryAfter?.Date is { } date)
        {
            var seconds = Math.Max(1, (int)Math.Ceiling((date - DateTimeOffset.UtcNow).TotalSeconds));
            return seconds.ToString(CultureInfo.InvariantCulture);
        }

        if (response.Headers.TryGetValues(HeaderNames.RetryAfter, out var rawValues))
        {
            var raw = rawValues.FirstOrDefault(static item => !string.IsNullOrWhiteSpace(item))?.Trim();
            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0)
            {
                return parsed.ToString(CultureInfo.InvariantCulture);
            }
        }

        return null;
    }
}
