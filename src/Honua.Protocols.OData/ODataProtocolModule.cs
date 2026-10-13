// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Infrastructure.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Protocols.OData;

/// <summary>
/// OData v4 production service and endpoint registration, reusable by protocol-specific hosts.
/// </summary>
public sealed class ODataProtocolModule : IHonuaProtocolModule
{
    /// <inheritdoc />
    public string Name => "OData";

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddOData(configuration);
    }

    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapODataEndpoints();
    }
}
