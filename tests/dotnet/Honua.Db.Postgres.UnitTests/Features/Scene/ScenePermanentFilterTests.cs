// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Db.Postgres.Features.Infrastructure;
using Honua.Db.Postgres.Features.Scene;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Db.Postgres.Tests.Features.Scene;

[global::Xunit.Trait("Tier", "Fast")]
public sealed class ScenePermanentFilterTests
{
    [Fact]
    public void Resolve_FailsClosedWhenTheFilterCannotBeTranslated()
    {
        var source = new PostgresSceneFeatureSource(
            new UnusedConnectionProvider(),
            NullLogger<PostgresSceneFeatureSource>.Instance);

        var act = () => source.ResolveScenePermanentFilter(new MetadataV2Object
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "roads" },
            PermanentFilter = new MetadataV2PermanentFilter { Expression = "classification <> 'secret'" }
        });

        Assert.Throws<InvalidOperationException>(act);
    }

    private sealed class UnusedConnectionProvider : IPrimaryDatabaseConnectionProvider
    {
        public string GetConnectionString() => "Host=localhost;Database=unused;";

        public Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Scene filter resolution does not open a connection.");

        public Task<(DbConnection Connection, DbTransaction Transaction)> OpenTransactionAsync(
            IsolationLevel isolationLevel = IsolationLevel.RepeatableRead,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Scene filter resolution does not open a connection.");

        public Task<T> ExecuteWithDeadlockRetryAsync<T>(
            Func<Task<T>> operation,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Scene filter resolution does not open a connection.");

        public Task ExecuteWithDeadlockRetryAsync(
            Func<Task> operation,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Scene filter resolution does not open a connection.");
    }

    [Fact]
    public void Resolve_SkipsLayersWithoutAPermanentFilter()
    {
        var source = new PostgresSceneFeatureSource(
            new UnusedConnectionProvider(),
            NullLogger<PostgresSceneFeatureSource>.Instance);

        Assert.Null(source.ResolveScenePermanentFilter(new MetadataV2Object
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "roads" }
        }));
    }
}
