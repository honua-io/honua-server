// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;

namespace Honua.Infrastructure;

internal static class OrdinalIgnoreCaseKey
{
    internal static string Create(string value)
    {
        var result = new StringBuilder(value.Length);
        foreach (var rune in value.EnumerateRunes())
        {
            var original = rune.ToString();
            var upper = Rune.ToUpperInvariant(rune).ToString();
            // Ordinal comparison excludes some invariant casing pairs, including s and long s.
            result.Append(original.Equals(upper, StringComparison.OrdinalIgnoreCase) ? upper : original);
        }
        return result.ToString();
    }
}
