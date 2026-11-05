// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.AspNetCore.Http;

namespace Honua.Infrastructure.Helpers;

internal static class TimeoutTokenHelper
{
    internal const string LimitsTimeoutTokenKey = "LimitsTimeoutToken";

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
