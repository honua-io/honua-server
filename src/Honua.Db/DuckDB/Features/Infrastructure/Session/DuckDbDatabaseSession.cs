// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data.Common;
using Honua.Core.Features.Infrastructure.Session;

namespace Honua.DuckDB.Features.Infrastructure.Session;

/// <summary>
/// DuckDB implementation of
/// <see cref="Honua.Core.Features.Infrastructure.Abstractions.IDatabaseSession"/>.
/// Inherits the provider-neutral ADO.NET plumbing from
/// <see cref="AdoNetDatabaseSession"/>.
/// </summary>
internal sealed class DuckDbDatabaseSession : AdoNetDatabaseSession
{
    public DuckDbDatabaseSession(DbConnection connection, DbTransaction? transaction = null)
        : base(connection, transaction, ownsConnection: true)
    {
    }
}
