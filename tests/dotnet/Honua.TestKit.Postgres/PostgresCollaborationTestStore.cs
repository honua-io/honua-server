// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Collaboration.Operations;
using Honua.Db.Postgres.Features.Collaboration.Operations;
using Honua.TestKit.Infrastructure;
using Npgsql;

namespace Honua.TestKit;

// Exercises the production shared operation store from server cross-task integration tests.
internal static class PostgresCollaborationTestStore
{
    public static ISavedMapOperationLogRepository Create(NpgsqlDataSource dataSource, string schema) =>
        new PostgresSavedMapOperationLogRepository(new TestDatabaseConnectionProvider(dataSource, () => schema),
            new MvpSavedMapOperationConflictPolicy(), schemaName: schema);
}
