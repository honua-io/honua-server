// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Validation;

public static class UriEscapeValidation
{
    public static bool ContainsMalformedEscapeSequence(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '%')
            {
                continue;
            }

            if (i + 2 >= value.Length)
            {
                return true;
            }

            if (!char.IsAsciiHexDigit(value[i + 1]) || !char.IsAsciiHexDigit(value[i + 2]))
            {
                return true;
            }

            i += 2;
        }

        return false;
    }
}
