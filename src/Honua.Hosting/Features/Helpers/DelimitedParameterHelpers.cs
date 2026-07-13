// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Infrastructure.Helpers;

internal static class DelimitedParameterHelpers
{
    internal static bool HasEmptyCommaSeparatedToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Split(',', StringSplitOptions.None).Any(token => token.Trim().Length == 0);
    }
}
