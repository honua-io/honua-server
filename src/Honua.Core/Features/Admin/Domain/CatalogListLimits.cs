// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Admin.Domain;

public static class CatalogListLimits
{
    public const int DefaultPage = 1;

    public const int DefaultPageSize = 100;

    public const int MaxPageSize = 100;

    public const int MaxNameQueryLength = 200;

    public static string? DescribeOutOfRange(int page, int pageSize, string? nameQuery)
    {
        if (page < DefaultPage)
        {
            return "'page' must be at least 1.";
        }

        if (pageSize < 1 || pageSize > MaxPageSize)
        {
            return $"'pageSize' must be from 1 to {MaxPageSize}.";
        }

        if (nameQuery is { Length: > MaxNameQueryLength })
        {
            return $"'q' must be at most {MaxNameQueryLength} characters.";
        }

        if (page > 1 && page - 1 > int.MaxValue / pageSize)
        {
            return "'page' is past the supported range.";
        }

        return null;
    }
}
