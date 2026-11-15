// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Queries.Filters;
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

    [Fact]
    public void Resolve_OmitsSavedFilterTextFromTheFailure()
    {
        const string marker = "saved-filter-marker-9f3c";
        var source = new PostgresSceneFeatureSource(
            new UnusedConnectionProvider(),
            NullLogger<PostgresSceneFeatureSource>.Instance,
            new EchoingFilterService(marker));

        var error = Assert.Throws<InvalidOperationException>(() => source.ResolveScenePermanentFilter(new MetadataV2Object
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "roads" },
            PermanentFilter = new MetadataV2PermanentFilter { Expression = $"classification = '{marker}'" }
        }));

        Assert.DoesNotContain(marker, error.ToString(), StringComparison.Ordinal);
        Assert.Null(error.InnerException);
    }

    private sealed class EchoingFilterService : IFilterExpressionService
    {
        private readonly string _marker;

        public EchoingFilterService(string marker) => _marker = marker;

        public FilterParseResult Parse(FilterLanguage language, string? filter)
            => FilterParseResult.Failure(_marker + " " + filter);

        public FilterParseResult ParseAndNormalize(FilterLanguage language, string? filter, MetadataV2Object resource)
            => FilterParseResult.Failure(_marker + " " + filter);

        public FilterExpression Normalize(FilterExpression expression, MetadataV2Object resource)
            => expression;

        public FilterTranslationResult Translate(FilterExpression? expression, MetadataV2Object resource)
            => FilterTranslationResult.Failure(_marker);
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
