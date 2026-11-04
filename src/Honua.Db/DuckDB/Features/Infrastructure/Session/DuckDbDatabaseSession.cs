// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Data.Common;
using Honua.Core.Features.Infrastructure.Session;

namespace Honua.Db.DuckDB.Features.Infrastructure.Session;

internal sealed class DuckDbDatabaseSession : AdoNetDatabaseSession
{
    public DuckDbDatabaseSession(DbConnection connection, DbTransaction? transaction = null)
        : base(connection, transaction, ownsConnection: true)
    {
    }
}
