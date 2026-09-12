// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Features.Operations;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

[Collection(RedisFixture.CollectionName)]
[Protocol(TestProtocols.Infrastructure)]
[Operation(Operations.TestInfrastructure)]
public sealed class RedisOperationSecretStoreIntegrationTests(RedisFixture redis)
{
    [IntegrationTest]
    public async Task RedisSecretChannel_StoresOnlyProtectedEnvelope_AndConsumesAcrossNodesOnce()
    {
        using var multiplexerA = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        using var multiplexerB = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var services = new ServiceCollection();
        services.AddDataProtection()
            .SetApplicationName("Honua.Tests.OperationSecrets");
        using var provider = services.BuildServiceProvider();
        var dataProtection = provider.GetRequiredService<IDataProtectionProvider>();
        var storeA = new RedisOperationSecretStore(multiplexerA, dataProtection);
        var storeB = new RedisOperationSecretStore(multiplexerB, dataProtection);
        var operationInstanceId = $"opinst-{Guid.NewGuid():N}";
        var operationId = "admin.api-key.create";
        var principalId = $"principal-{Guid.NewGuid():N}";
        var tenantId = $"tenant-{Guid.NewGuid():N}";
        var secret = Guid.NewGuid().ToString("N");

        var reference = storeA.Store(
            operationInstanceId,
            operationId,
            principalId,
            tenantId,
            "key",
            secret);

        var server = multiplexerA.GetServer(multiplexerA.GetEndPoints().Single());
        var key = server.Keys(pattern: "controlplane:operation-secret:*")
            .Single(item => item.ToString().EndsWith(reference.ReferenceId, StringComparison.Ordinal));
        var values = (await multiplexerA.GetDatabase().HashGetAllAsync(key))
            .Select(entry => entry.Value.ToString())
            .ToArray();
        values.Should().NotContain(secret);

        storeB.Consume(reference, operationInstanceId, operationId, principalId, tenantId)
            .Should().Be(secret);
        storeA.Consume(reference, operationInstanceId, operationId, principalId, tenantId)
            .Should().BeNull();
        server.Keys(pattern: $"controlplane:operation-secret:*{reference.ReferenceId}")
            .Should().BeEmpty();
    }
}
