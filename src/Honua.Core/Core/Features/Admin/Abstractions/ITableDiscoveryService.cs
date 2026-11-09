// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Data.Common;
using Honua.Core.Features.Admin.Domain;

namespace Honua.Core.Features.Admin.Abstractions;

public interface ITableDiscoveryService
{
    Task<List<TableInfo>> DiscoverPostGisTablesAsync(
        string connectionString,
        CancellationToken cancellationToken = default);

    Task<List<TableInfo>> DiscoverPostGisTablesAsync(
        DbConnection connection,
        CancellationToken cancellationToken = default);
    Task<TableInfo?> DiscoverNonSpatialTableAsync(
        DbConnection connection,
        string schema,
        string table,
        CancellationToken cancellationToken = default)
        => Task.FromResult<TableInfo?>(null);
}
