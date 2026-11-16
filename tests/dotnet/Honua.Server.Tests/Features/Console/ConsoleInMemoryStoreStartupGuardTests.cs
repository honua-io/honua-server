// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.Console;
using Honua.Core.Features.Console.Domain;
using Honua.Server.Features.Console;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.Console;

[Protocol(TestProtocols.Admin)]
[Operation(Operations.RoleManagement)]
public sealed class ConsoleInMemoryStoreStartupGuardTests
{
    public static TheoryData<Dictionary<string, string?>> SeveralTaskConfigurations => new()
    {
        new() { [ConsoleStoreReplicaPolicy.ConsoleExpectedReplicaCountKey] = "1", [DeploymentTaskCount.ReplicaCountKey] = "4" },
        new() { [ConsoleStoreReplicaPolicy.ConsoleExpectedReplicaCountKey] = "1", [DeploymentTaskCount.ServerReplicasKey] = "3" },
        new() { [ConsoleStoreReplicaPolicy.ConsoleExpectedReplicaCountKey] = "1", [DeploymentTaskCount.MaxTasksKey] = "8" },
        new() { [DeploymentTaskCount.ModeKey] = "MultiNode" },
        new() { [ConsoleStoreReplicaPolicy.ConsoleExpectedReplicaCountKey] = "5", [DeploymentTaskCount.ReplicaCountKey] = "1" },
    };

    public static TheoryData<Dictionary<string, string?>> SingleTaskConfigurations => new()
    {
        new(),
        new() { [DeploymentTaskCount.ReplicaCountKey] = "1", [DeploymentTaskCount.MaxTasksKey] = "1", [DeploymentTaskCount.ModeKey] = "SingleInstance" },
        new() { [ConsoleStoreReplicaPolicy.ConsoleExpectedReplicaCountKey] = "0", [DeploymentTaskCount.ReplicaCountKey] = "invalid" },
    };

    [UnitTheory]
    [MemberData(nameof(SeveralTaskConfigurations))]
    public async Task StartAsync_SeveralConfiguredTasks_RequiresDurableStores(Dictionary<string, string?> settings)
    {
        var guard = CreateGuard(Configuration(settings), ConsolePersistenceNotice.InMemory);

        var start = () => guard.StartAsync(CancellationToken.None);

        await start.Should().ThrowAsync<InvalidOperationException>();
    }

    [UnitTheory]
    [MemberData(nameof(SingleTaskConfigurations))]
    public async Task StartAsync_SingleTask_AllowsInMemoryStores(Dictionary<string, string?> settings)
    {
        var guard = CreateGuard(Configuration(settings), ConsolePersistenceNotice.InMemory);

        await guard.StartAsync(CancellationToken.None);
        await guard.StopAsync(CancellationToken.None);
    }

    [UnitTest]
    public async Task StartAsync_DurableStores_AllowsSeveralTasks()
    {
        var notice = new ConsolePersistenceNotice { Mode = "durable", Durable = true, SharedAcrossReplicas = true };
        var guard = CreateGuard(Configuration(new() { [DeploymentTaskCount.ReplicaCountKey] = "4", [DeploymentTaskCount.MaxTasksKey] = "8" }), notice);

        await guard.StartAsync(CancellationToken.None);
    }

    private static ConsoleInMemoryStoreStartupGuard CreateGuard(IConfiguration configuration, ConsolePersistenceNotice notice)
        => new(NullLogger<ConsoleInMemoryStoreStartupGuard>.Instance, configuration, notice);

    private static IConfiguration Configuration(Dictionary<string, string?> settings)
        => new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
}
