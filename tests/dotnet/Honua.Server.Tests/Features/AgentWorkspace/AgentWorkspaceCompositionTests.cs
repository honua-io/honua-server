// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Server.Features.AgentWorkspace;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.AgentWorkspace;

public sealed class AgentWorkspaceCompositionTests
{
    [UnitTest]
    public async Task AddLocalProcessWorkspaces_WithoutSystemDatabase_ComposesAndReturnsUnavailable()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddLocalProcessWorkspaces();
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
        using var scope = provider.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<AgentWorkspaceAuthorization>());
        var store = provider.GetRequiredService<AgentWorkspaceIdentityStore>();
        var read = await Assert.ThrowsAsync<LocalProcessWorkspaceException>(() => store.GetAsync("workspace", CancellationToken.None));
        var write = await Assert.ThrowsAsync<LocalProcessWorkspaceException>(() => store.CreateAsync(
            new AgentWorkspaceIdentity("workspace", "owner", "target", null), CancellationToken.None));
        Assert.Equal(503, read.StatusCode);
        Assert.Equal(503, write.StatusCode);
    }
}
