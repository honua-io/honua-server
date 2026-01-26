// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.AspNetCore.Http;

namespace Honua.Server.Features.Infrastructure.Helpers;

/// <summary>
/// Provides shared access to timeout-aware cancellation tokens.
/// </summary>
internal static class TimeoutTokenHelper
{
    internal const string LimitsTimeoutTokenKey = "LimitsTimeoutToken";

    /// <summary>
    /// Gets a timeout-aware cancellation token from the HTTP context.
    /// Prefers the limits timeout token if available, otherwise uses request cancellation.
    /// </summary>
    public static CancellationToken GetTimeoutAwareCancellationToken(HttpContext context)
    {
        if (context.Items.TryGetValue(LimitsTimeoutTokenKey, out var tokenObj) &&
            tokenObj is CancellationToken timeoutToken)
        {
            return timeoutToken;
        }

        return context.RequestAborted;
    }
}
