// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Deployment.Domain;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Import;
using Honua.Server.Features.Migration;
using Honua.Import.FileImport;
using Honua.Import.RasterImport;
using Honua.Server.Features.Infrastructure.Progress;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;

namespace Honua.Server.Tests.Import;

[Collection("Redis")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.TestInfrastructure)]
public sealed class UniversalProgressStoreIntegrationTests
{
    private readonly RedisFixture _redis;

    public UniversalProgressStoreIntegrationTests(RedisFixture redis)
    {
        _redis = redis;
    }

    [IntegrationTest]
    public async Task GetActiveOperationIdsAsync_WithRedisIndex_ReturnsTrackedOperationIds()
    {
        using var provider = BuildRedisServices();
        using var multiplexer = ConnectionMultiplexer.Connect(_redis.ConnectionString);
        var cache = provider.GetRequiredService<IDistributedCache>();
        var store = new UniversalProgressStore(cache, NullLogger<UniversalProgressStore>.Instance, multiplexer);

        await store.SetProgressAsync(
            "export-1",
            ExportProgress.CreateInitial("export-1", "csv", "svc", 1, 10),
            TimeSpan.FromMinutes(5));

        var activeIds = await store.GetActiveOperationIdsAsync(OperationType.Export);

        activeIds.Should().Contain("export-1");
    }

    [IntegrationTest]
    [FlakyTest("RedisFixture lifecycle race during DeleteProgressAsync teardown — distributed progress state intermittently unavailable when the shared Redis collection fixture is being recycled across parallel tests. Tracked in #812 quarantine sweep.")]
    public async Task SetAndGetProgressAsync_WithDeploymentProgress_RoundTripsThroughRedis()
    {
        using var provider = BuildRedisServices();
        using var multiplexer = ConnectionMultiplexer.Connect(_redis.ConnectionString);
        var cache = provider.GetRequiredService<IDistributedCache>();
        var store = new UniversalProgressStore(cache, NullLogger<UniversalProgressStore>.Instance, multiplexer);

        var operationId = $"deploy-{Guid.NewGuid():N}";
        var initial = DeploymentProgress
            .CreateRollingOut(operationId, "dep-001", RolloutState.InProgress) with
        {
            PercentComplete = 42,
            Warnings = ["health-check pending"]
        };

        try
        {
            await store.SetProgressAsync(operationId, initial, TimeSpan.FromMinutes(5));

            var roundTripped = await store.GetProgressAsync<DeploymentProgress>(operationId);

            roundTripped.Should().NotBeNull();
            roundTripped!.OperationId.Should().Be(operationId);
            roundTripped.DeploymentId.Should().Be("dep-001");
            roundTripped.DeploymentStatus.Should().Be(DeploymentStatus.RollingOut);
            roundTripped.RolloutState.Should().Be(RolloutState.InProgress);
            roundTripped.PercentComplete.Should().Be(42);
            roundTripped.Warnings.Should().ContainSingle().Which.Should().Be("health-check pending");
            ((IOperationProgress)roundTripped).Type.Should().Be(OperationType.Deployment);

            var activeIds = await store.GetActiveOperationIdsAsync(OperationType.Deployment);
            activeIds.Should().Contain(operationId);
        }
        finally
        {
            await store.DeleteProgressAsync(operationId);
        }
    }

    private ServiceProvider BuildRedisServices()
    {
        var services = new ServiceCollection();
        services.AddStackExchangeRedisCache(options =>
        {
            options.Configuration = _redis.ConnectionString;
            options.InstanceName = string.Empty;
        });

        return services.BuildServiceProvider();
    }
}
