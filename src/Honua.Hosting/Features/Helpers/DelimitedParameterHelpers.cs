// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

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
