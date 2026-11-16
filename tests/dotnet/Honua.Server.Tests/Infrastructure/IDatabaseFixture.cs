// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.TestKit;

namespace Honua.Server.Tests.Infrastructure;

public interface IDatabaseFixture : IIsolatedSchemaFixture, IAsyncLifetime
{
    Task ExecuteAsync(string sql, string? schemaName = null);

    Task CreateSchemaUnderLockAsync(string schemaName);

    Task ApplyGlobalSeedSqlAsync(string sql, string? schemaName = null);

    Task<DbUp.Engine.DatabaseUpgradeResult> RunEmbeddedMigrationsUnderLockAsync(
        string schemaName,
        System.Reflection.Assembly migrationsAssembly);
}
