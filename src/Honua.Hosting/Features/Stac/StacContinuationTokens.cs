// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;

namespace Honua.Infrastructure.Stac;

/// <summary>
/// STAC Item Search continuation cursors. The query name is <c>next</c> so a cursor
/// is not read as an ArcGIS portal <c>token</c> credential.
/// </summary>
internal static class StacContinuationTokens
{
    internal const string Prefix = "offset:";

    internal const string QueryParameter = "next";

    internal static bool IsCursor(string? token)
        => TryParse(token, out _);

    internal static bool TryParse(string? token, out int offset)
    {
        offset = 0;
        if (string.IsNullOrWhiteSpace(token) ||
            !token.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var value = token.AsSpan(Prefix.Length);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out offset) &&
               offset >= 0;
    }
}
