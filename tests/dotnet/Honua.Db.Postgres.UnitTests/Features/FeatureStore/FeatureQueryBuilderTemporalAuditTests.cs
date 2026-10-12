// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;
using Honua.Db.Postgres.Features.FeatureStore.Services;
using Microsoft.Extensions.ObjectPool;
using FeatureStoreStringBuilderPooledObjectPolicy = Honua.Db.Postgres.Features.FeatureStore.Services.StringBuilderPooledObjectPolicy;

namespace Honua.Db.Postgres.Tests.Features.FeatureStore;

public sealed class FeatureQueryBuilderTemporalAuditTests
{
    [Fact]
    public void SRV_DB_004_TemporalFilterAndDateBinsUseEpochAwareExpressions()
    {
        var builder = new FeatureQueryBuilder(
            new DefaultObjectPoolProvider().Create(new FeatureStoreStringBuilderPooledObjectPolicy()),
            new GeometryProcessor());
        var query = new FeatureQuery
        {
            TemporalFilter = new TemporalFilter
            {
                PropertyName = "observed_at",
                PropertyType = TemporalPropertyType.DateTime,
                Start = DateTimeOffset.UnixEpoch,
            },
        };

        var pageSql = builder.BuildSelectQuery(1, query).Sql;
        var binsSql = builder.BuildDateBinsQuery(1, new FeatureQuery(), new DateBinDefinition
        {
            BinField = "observed_at",
            CalendarUnit = "day",
        }).Sql;

        pageSql.Should().Contain("to_timestamp").And.Contain("/ 1000.0");
        binsSql.Should().Contain("to_timestamp").And.Contain("/ 1000.0");
    }
}
