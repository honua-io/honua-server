// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Server.Features.Protocols.OData;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Features.Infrastructure.Hosting.Modules;

/// <summary>
/// OData v4 protocol module. Wraps the existing
/// <see cref="ODataServiceCollectionExtensions.AddOData"/> and
/// <see cref="ODataEndpoints.MapODataEndpoints"/> registration calls behind
/// the <see cref="IHonuaProtocolModule"/> contract so the host can iterate
/// protocols uniformly. The wrapper is additive — the existing direct calls
/// in <c>FeatureRegistrationExtensions</c> still own the canonical wiring
/// until a follow-up PR migrates them to module-based dispatch.
/// </summary>
public sealed class ODataProtocolModule : IHonuaProtocolModule
{
    /// <inheritdoc />
    public string Name => "OData";

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddOData();
    }

    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapODataEndpoints();
    }
}
