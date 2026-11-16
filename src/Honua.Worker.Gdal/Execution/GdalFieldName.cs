// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Worker.Gdal.Execution;

internal static class GdalFieldName
{
    public static bool IsValid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var token = value.Trim();
        if (token.Length > 128)
        {
            return false;
        }

        if (!(char.IsAsciiLetter(token[0]) || token[0] == '_'))
        {
            return false;
        }

        return token.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
    }
}
