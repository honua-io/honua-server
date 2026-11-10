// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.FeatureStore.Domain;

// This checks syntax only; quote characters, whitespace, semicolons, and control characters stay excluded.
public static class FeatureFieldNameSyntax
{
    public static bool IsValid(string? fieldName)
    {
        if (string.IsNullOrWhiteSpace(fieldName))
        {
            return false;
        }

        for (var i = 0; i < fieldName.Length; i++)
        {
            var ch = fieldName[i];
            var allowed = char.IsLetterOrDigit(ch)
                || ch == '_'
                || (i > 0 && (ch == ':' || ch == '.' || ch == '-'));
            if (!allowed)
            {
                return false;
            }
        }

        return true;
    }
}
