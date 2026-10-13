// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Protocols.OData;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Infrastructure.Hosting.Modules;

/// <summary>
/// Compatibility wrapper delegating to the production OData assembly module.
/// </summary>
public sealed class ODataProtocolModule : IHonuaProtocolModule
{
    private readonly Honua.Protocols.OData.ODataProtocolModule _module = new();

    /// <inheritdoc />
    public string Name => _module.Name;

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        _module.ConfigureServices(services, configuration);
    }

    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        _module.MapEndpoints(endpoints);
    }
}
