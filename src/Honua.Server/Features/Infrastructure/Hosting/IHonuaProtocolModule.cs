// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Features.Infrastructure.Hosting;

/// <summary>
/// Plug-in contract for a protocol surface (modularization Phase 1).
/// Each protocol that ships as its own assembly exports exactly one implementation;
/// the host discovers them by reflection over loaded assemblies and a config-driven
/// enable/disable list (<c>Protocols:Enabled</c>).
/// </summary>
/// <remarks>
/// The interface intentionally lives in <c>Honua.Server</c> (not
/// <c>Honua.Core.Abstractions</c>) because it depends on ASP.NET Core types
/// (<see cref="IEndpointRouteBuilder"/>). Adding the AspNetCore framework
/// reference to the contract-only assembly was rejected as overreach; the
/// trade-off is that future protocol assemblies will need to reference
/// Server's hosting surface alongside <c>Honua.Core.Abstractions</c>. See
/// ADR-0041 for the Phase 0 / Phase 1 sequencing.
/// </remarks>
public interface IHonuaProtocolModule
{
    /// <summary>
    /// Stable identifier for this protocol. Matches the config-list entry
    /// (e.g. <c>OData</c>, <c>OgcApi</c>, <c>OgcClassic</c>, <c>GeoServices</c>).
    /// Used by the host to decide whether to wire the module.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Registers the protocol's services with the DI container. Called once at
    /// startup, before any endpoint mapping.
    /// </summary>
    void ConfigureServices(IServiceCollection services, IConfiguration configuration);

    /// <summary>
    /// Maps the protocol's HTTP endpoints. Called once at startup, after
    /// <see cref="ConfigureServices"/> on every registered module has run.
    /// </summary>
    void MapEndpoints(IEndpointRouteBuilder endpoints);
}
