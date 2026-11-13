// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Db.Postgres.Features.Infrastructure;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Configuration;

namespace Honua.Server.Tests.Infrastructure;

[Protocol(TestProtocols.TestQuality)]
public sealed class DeploymentConnectionPoolSizingTests
{
    [UnitTest]
    public void ResolveConnectionLimits_NothingStated_KeepsServingDefaults()
    {
        var limits = PostgresDataSourceFactory.ResolveConnectionLimits(Configuration());

        var defaults = new ConnectionLimits();
        limits.MaxConnectionPoolSize.Should().Be(defaults.MaxConnectionPoolSize);
        limits.MinConnectionPoolSize.Should().Be(defaults.MinConnectionPoolSize);
        limits.MaxConcurrentQueries.Should().Be(defaults.MaxConcurrentQueries);
    }

    [UnitTheory]
    [InlineData("db.t4g.small", 1, 72)]
    [InlineData("db.t4g.small", 2, 36)]
    [InlineData("db.t4g.small", 10, 7)]
    [InlineData("db.t4g.medium", 2, 72)]
    [InlineData("db.m7g.large", 10, 28)]
    [InlineData(null, 2, 20)]
    public void ResolveConnectionLimits_TasksAndInstanceClass_SizesEachPoolFromTheDatabaseLimit(
        string? instanceClass, int tasks, int expectedMaxPool)
    {
        var limits = PostgresDataSourceFactory.ResolveConnectionLimits(Configuration(
            (DeploymentTaskCount.MaxTasksKey, tasks.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            (DeploymentConnectionPoolSizing.DatabaseInstanceClassKey, instanceClass)));

        limits.MaxConnectionPoolSize.Should().Be(expectedMaxPool);
        limits.MinConnectionPoolSize.Should().Be(DeploymentConnectionPoolSizing.IdleConnectionsPerPool);
        limits.MaxConcurrentQueries.Should().Be(expectedMaxPool);
    }

    [UnitTest]
    public void ResolveConnectionLimits_TwoTasksOnSmallestLaunchStackDatabase_StayWithinItsConnectionLimit()
    {
        var configuration = Configuration(
            (DeploymentTaskCount.MaxTasksKey, "2"),
            (DeploymentConnectionPoolSizing.DatabaseInstanceClassKey, "db.t4g.small"));

        var limits = PostgresDataSourceFactory.ResolveConnectionLimits(configuration);

        var heldAtPeak = 2 * DeploymentConnectionPoolSizing.PoolsPerTask * limits.MaxConnectionPoolSize;
        heldAtPeak.Should().BeLessThanOrEqualTo(DeploymentConnectionPoolSizing.ResolveMaxConnections(configuration)!.Value);
    }

    [UnitTest]
    public void ResolveConnectionLimits_OperatorValues_AreLeftAlone()
    {
        var limits = PostgresDataSourceFactory.ResolveConnectionLimits(Configuration(
            (DeploymentTaskCount.MaxTasksKey, "10"),
            (DeploymentConnectionPoolSizing.DatabaseInstanceClassKey, "db.t4g.small"),
            ("Limits:Connections:MaxConnectionPoolSize", "50"),
            ("Limits:Connections:MinConnectionPoolSize", "5"),
            ("Limits:Connections:MaxConcurrentQueries", "120")));

        limits.MaxConnectionPoolSize.Should().Be(50);
        limits.MinConnectionPoolSize.Should().Be(5);
        limits.MaxConcurrentQueries.Should().Be(120);
    }

    [UnitTest]
    public void ResolveConnectionLimits_StatedMaxConnections_WinsOverInstanceClass()
    {
        var limits = PostgresDataSourceFactory.ResolveConnectionLimits(Configuration(
            (DeploymentTaskCount.MaxTasksKey, "2"),
            (DeploymentConnectionPoolSizing.DatabaseInstanceClassKey, "db.t4g.small"),
            (DeploymentConnectionPoolSizing.DatabaseMaxConnectionsKey, "1000")));

        limits.MaxConnectionPoolSize.Should().Be(200);
    }

    [UnitTest]
    public void ResolveConnectionLimits_ExecutionWorker_KeepsItsOwnSmallPool()
    {
        var limits = PostgresDataSourceFactory.ResolveConnectionLimits(Configuration(
            (DeploymentTaskCount.MaxTasksKey, "2"),
            (DeploymentConnectionPoolSizing.DatabaseInstanceClassKey, "db.m7g.large"),
            (PostgresDataSourceFactory.ExecutionWorkerOperationIdVariable, "job-1")));

        limits.MaxConnectionPoolSize.Should().Be(PostgresDataSourceFactory.ExecutionWorkerUnsetMaxConnectionPoolSize);
    }

    [UnitTheory]
    [InlineData("db.t4g.micro", 1)]
    [InlineData("db.t4g.small", 2)]
    [InlineData("DB.T3.MEDIUM", 4)]
    [InlineData("db.m7g.large", 8)]
    [InlineData("db.m6i.xlarge", 16)]
    [InlineData("db.r6g.2xlarge", 64)]
    [InlineData("db.x2g.large", null)]
    [InlineData("t4g.small", null)]
    [InlineData("db.m7g.hugexlarge", null)]
    [InlineData("", null)]
    public void MemoryGibibytes_InstanceClass_ReadsSize(string instanceClass, int? expected)
        => DeploymentConnectionPoolSizing.MemoryGibibytes(instanceClass).Should().Be(expected);

    private static IConfiguration Configuration(params (string Key, string? Value)[] settings)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(setting => new KeyValuePair<string, string?>(setting.Key, setting.Value)))
            .Build();
}
