// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;

namespace Honua.Core.Features.Shared.Models;

public readonly record struct CoordinateSystemSearchQuery(string Text, int ExactCode, int Limit)
{
    public static CoordinateSystemSearchQuery Parse(string query, int limit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        query = query.Trim();
        if (query.Length > 128) throw new ArgumentOutOfRangeException(nameof(query));
        if (limit is < 1 or > 50) throw new ArgumentOutOfRangeException(nameof(limit));
        var codeText = query.StartsWith("EPSG:", StringComparison.OrdinalIgnoreCase) ? query[5..] : query;
        var code = int.TryParse(codeText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
        return new(query, code, limit);
    }
}
