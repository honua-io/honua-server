// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Data.Common;
using Honua.Core.Features.Infrastructure.Session;

namespace Honua.Db.Postgres.Features.Infrastructure.Session;

/// <summary>
/// PostgreSQL implementation of <see cref="Honua.Core.Features.Infrastructure.Abstractions.IDatabaseSession"/>.
/// Delegates to <see cref="AdoNetDatabaseSession"/> for the provider-neutral
/// command/reader/transaction plumbing.
/// </summary>
internal sealed class PostgresDatabaseSession : AdoNetDatabaseSession
{
    public PostgresDatabaseSession(DbConnection connection, DbTransaction? transaction = null)
        : base(connection, transaction, ownsConnection: true)
    {
    }
}
