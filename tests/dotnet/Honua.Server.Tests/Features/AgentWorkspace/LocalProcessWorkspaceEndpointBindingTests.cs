// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Server.Features.AgentWorkspace;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.AgentWorkspace;

public sealed class LocalProcessWorkspaceEndpointBindingTests
{
    // Minimal APIs treat a parameter type that declares a method named BindAsync as a custom binder,
    // and refuse to build the endpoint unless that method has the binder shape. The server then
    // fails at startup when it builds its endpoints.
    [UnitTest]
    public void GatewayParameter_IsBoundFromServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILocalProcessWorkspaceGateway>(_ => throw new InvalidOperationException("Not resolved while building."));
        using var provider = services.BuildServiceProvider();

        var result = RequestDelegateFactory.Create(
            (ILocalProcessWorkspaceGateway gateway) => Results.NoContent(),
            new RequestDelegateFactoryOptions { ServiceProvider = provider });

        Assert.NotNull(result.RequestDelegate);
    }
}
