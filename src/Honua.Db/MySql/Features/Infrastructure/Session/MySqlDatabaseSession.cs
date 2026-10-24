// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Data.Common;
using Honua.Core.Features.Infrastructure.Session;

namespace Honua.Db.MySql.Features.Infrastructure.Session;

/// <summary>
/// MySQL/MariaDB implementation of
/// <see cref="Honua.Core.Features.Infrastructure.Abstractions.IDatabaseSession"/>.
/// Inherits the provider-neutral ADO.NET plumbing from
/// <see cref="AdoNetDatabaseSession"/>.
/// </summary>
internal sealed class MySqlDatabaseSession : AdoNetDatabaseSession
{
    public MySqlDatabaseSession(DbConnection connection, DbTransaction? transaction = null)
        : base(connection, transaction, ownsConnection: true)
    {
    }
}
