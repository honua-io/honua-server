// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Reflection;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Db.Postgres.Features.Alerts;
using Honua.TestKit.Attributes;
using NSubstitute;

namespace Honua.Server.Tests.Features.Alerts;

public sealed partial class AlertChangeGenerationEvaluationTests
{
    [IntegrationTheory(Timeout = 180000)]
    [InlineData(1)]
    [InlineData(10)]
    public async Task GetChangesAfterAsync_DelayedPrefix_DefersLaterRowsUntilPrefixAdvances(int maxCount)
    {
        const int layerId = 386413;
        const long objectId = 38641301;
        var schema = await database.CreateIsolatedSchemaAsync(nameof(GetChangesAfterAsync_DelayedPrefix_DefersLaterRowsUntilPrefixAdvances));
        var migration = await database.RunEmbeddedMigrationsUnderLockAsync(schema, Assembly.GetAssembly(typeof(Program))!);
        migration.Successful.Should().BeTrue(migration.Error?.ToString());
        await database.DropSchemaAsync(schema);
        var features = await FindFeatureTableAsync();
        await CleanupLayerAsync(features, layerId);
        try
        {
            await InsertJsonFeatureAsync(features, layerId, objectId, "{\"value\":1}");
            var first = await ScalarAsync<long>("SELECT MAX(generation) FROM honua.feature_changes");
            await InsertJsonFeatureAsync(features, layerId, objectId + 1, "{\"value\":2}");
            var second = await ScalarAsync<long>("SELECT MAX(generation) FROM honua.feature_changes");
            var tracker = Substitute.For<IChangeTracker>();
            tracker.GetCurrentGenerationAsync(Arg.Any<CancellationToken>()).Returns(first);
            var reader = new PostgresAlertChangeReader(new TestConnectionProvider(database.DataSource), null, tracker);

            var initial = await reader.GetChangesAfterAsync(first - 1, maxCount);
            initial.Select(change => change.Generation).Should().Equal(first);
            var deferred = await reader.GetChangesAfterAsync(first, maxCount);
            deferred.Should().BeEmpty("a checkpoint must not pass the captured committed prefix");
            tracker.GetCurrentGenerationAsync(Arg.Any<CancellationToken>()).Returns(second);
            var resumed = await reader.GetChangesAfterAsync(first, maxCount);
            resumed.Select(change => change.Generation).Should().Equal(second);
            await tracker.Received(3).GetCurrentGenerationAsync(Arg.Any<CancellationToken>());
        }
        finally
        {
            await CleanupLayerAsync(features, layerId);
        }
    }
}
