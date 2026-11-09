// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Catalog;

namespace Honua.Server.Features.Catalog;

internal sealed record CatalogSearchParameters(CatalogSearch Search, string? Bbox, string? Datetime)
{
    public static CatalogSearchParameters? Parse(IQueryCollection query)
    {
        var offset = Integer(query, "offset", 0);
        var limit = Integer(query, "limit", 50);
        var sort = Scalar(query, "sort") ?? "title";
        if (offset is null or < 0 || limit is null or < 1 or > CatalogSearch.MaximumPageSize || sort is not ("title" or "-updated")) return null;
        var search = new CatalogSearch
        {
            Text = Scalar(query, "q"), Kinds = Values(query, "kind"), Protocols = Values(query, "protocol"),
            Tags = Values(query, "tag"), Owners = Values(query, "owner"), Categories = Values(query, "category"),
            FolderId = Scalar(query, "folderId"), Status = Scalar(query, "status"),
            Offset = offset.Value, Limit = limit.Value, Sort = sort
        };
        return new(search, Scalar(query, "bbox"), Scalar(query, "datetime"));
    }

    private static int? Integer(IQueryCollection query, string key, int fallback)
        => !query.ContainsKey(key) ? fallback : int.TryParse(query[key].ToString(), NumberStyles.None,
            CultureInfo.InvariantCulture, out var value) ? value : null;

    private static string? Scalar(IQueryCollection query, string key)
        => string.IsNullOrWhiteSpace(query[key]) ? null : query[key].ToString();

    private static string[] Values(IQueryCollection query, string key)
        => query[key].Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!).ToArray();
}
