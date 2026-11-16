// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Admin.Domain;

namespace Honua.Server.Features.Admin;

internal readonly record struct AdminListQuery(int Page, int PageSize, string? NameQuery, string FolderId = "")
{
    public Guid? ConnectionId { get; init; }

    public int? StorageLayerId { get; init; }

    public Guid? ManagedConnectionId { get; init; }
}

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

        var scope = ParseLayerScope(query);
        if (scope.Error is not null)
        {
            error = scope.Error;
            return false;
        }

        listQuery = new AdminListQuery(resolvedPage, resolvedPageSize, nameQuery, query["folderId"].ToString())
        {
            ConnectionId = scope.ConnectionId,
            StorageLayerId = scope.StorageLayerId,
        };
        error = string.Empty;
        return true;
    }

    private static (Guid? ConnectionId, int? StorageLayerId, string? Error) ParseLayerScope(IQueryCollection query)
    {
        if (!query.ContainsKey("connectionId") && !query.ContainsKey("storageLayerId"))
        {
            return (null, null, null);
        }

        if (!query.TryGetValue("connectionId", out var connectionValues) || connectionValues.Count != 1
            || !Guid.TryParse(connectionValues[0], out var connectionId) || connectionId == Guid.Empty
            || !query.TryGetValue("storageLayerId", out var layerValues) || layerValues.Count != 1
            || !int.TryParse(layerValues[0], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var storageLayerId))
        {
            return (null, null, "'connectionId' and 'storageLayerId' must be supplied together as a nonempty UUID and nonnegative integer.");
        }

        return (connectionId, storageLayerId, null);
    }

}
