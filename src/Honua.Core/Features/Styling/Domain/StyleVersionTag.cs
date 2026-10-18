// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;

namespace Honua.Core.Features.Styling.Domain;

/// <summary>Exact canonical revision token used by shared-style conditional writes.</summary>
public static class StyleVersionTag
{
    /// <summary>Formats a nonnegative canonical style revision.</summary>
    public static string Format(int version)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(version);
        return "\"style-v" + version.ToString(CultureInfo.InvariantCulture) + "\"";
    }

    /// <summary>Accepts one exact revision token; wildcards, weak tags and lists are refused.</summary>
    public static bool TryParse(string? token, out int version)
    {
        version = 0;
        return token is { Length: >= 10 } && token.StartsWith("\"style-v", StringComparison.Ordinal) &&
            token.EndsWith('"') && int.TryParse(token.AsSpan(8, token.Length - 9), NumberStyles.None,
                CultureInfo.InvariantCulture, out version) && version >= 0 && Format(version) == token;
    }
}
