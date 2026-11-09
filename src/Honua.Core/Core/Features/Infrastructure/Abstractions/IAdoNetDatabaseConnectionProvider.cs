// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Data;
using System.Data.Common;

namespace Honua.Core.Features.Infrastructure.Abstractions;

// This escape hatch stays in Honua.Core so the abstractions package does not take a dependency on System.Data.Common.
public interface IAdoNetDatabaseConnectionProvider : IDatabaseConnectionProvider
{
    Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default);

    Task<(DbConnection Connection, DbTransaction Transaction)> OpenTransactionAsync(
        IsolationLevel isolationLevel = IsolationLevel.RepeatableRead,
        CancellationToken cancellationToken = default);
}
