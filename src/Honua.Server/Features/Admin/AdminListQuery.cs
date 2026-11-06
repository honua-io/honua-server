// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Admin.Domain;

namespace Honua.Server.Features.Admin;

internal readonly record struct AdminListQuery(int Page, int PageSize, string? NameQuery, string FolderId = "");

internal static class AdminListQueryParser
{
    internal static bool TryParse(IQueryCollection query, out AdminListQuery listQuery, out string error)
    {
        listQuery = default;
        if (!QueryFilterParsers.TryParseInt(query, "page", out var page, out error)
            || !QueryFilterParsers.TryParseInt(query, "pageSize", out var pageSize, out error))
        {
            return false;
        }

        var nameQuery = QueryFilterParsers.GetString(query, "q")?.Trim();
        if (string.IsNullOrEmpty(nameQuery))
        {
            nameQuery = null;
        }

        var resolvedPage = page ?? CatalogListLimits.DefaultPage;
        var resolvedPageSize = pageSize ?? CatalogListLimits.DefaultPageSize;
        var rejection = CatalogListLimits.DescribeOutOfRange(resolvedPage, resolvedPageSize, nameQuery);
        if (rejection is not null)
        {
            error = rejection;
            return false;
        }

        listQuery = new AdminListQuery(resolvedPage, resolvedPageSize, nameQuery, query["folderId"].ToString());
        error = string.Empty;
        return true;
    }
}
