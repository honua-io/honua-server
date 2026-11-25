// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Db.Postgres.Features.Raster;

internal static class RasterMetadataHelpers
{
    public static T? ResolveSingleValue<T>(IEnumerable<T?> values)
        where T : struct
    {
        T? single = null;
        foreach (var value in values)
        {
            if (!value.HasValue)
            {
                continue;
            }

            if (!single.HasValue)
            {
                single = value.Value;
            }
            else if (!EqualityComparer<T>.Default.Equals(single.Value, value.Value))
            {
                return null;
            }
        }

        return single;
    }

    public static string? ResolveSingleString(IEnumerable<string?> values)
    {
        string? single = null;
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (single is null)
            {
                single = value;
            }
            else if (!single.Equals(value, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }

        return single;
    }
}
