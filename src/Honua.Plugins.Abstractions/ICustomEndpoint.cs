// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Plugins.Abstractions;

/// <summary>
/// A plugin extension point that contributes an additional REST route to the server (issue #1562).
/// The host maps each registered endpoint via its <c>MapHonuaPluginEndpoints()</c> extension using
/// the AOT-safe <c>MapMethods</c> + explicit HTTP-metadata pattern, so contributed routes are
/// statically rooted (no runtime route discovery) and pass through the shared authorization,
/// validation, and telemetry middleware.
/// </summary>
/// <remarks>
/// <para>
/// The contract is deliberately ASP.NET-free so the public plugin SDK package stays
/// dependency-minimal and Native-AOT compatible: a plugin handler receives a
/// <see cref="PluginEndpointRequest"/> and returns a <see cref="PluginEndpointResponse"/>; the host
/// adapts these to/from <c>HttpContext</c>. Implementations are resolved as singletons and must be
/// thread-safe.
/// </para>
/// <para>
/// Contributing endpoints requires the <see cref="PluginCapability.CustomEndpoints"/> capability to
/// be declared on the plugin's <c>[Plugin]</c> manifest; the host refuses to register an endpoint
/// from a plugin that has not declared it. The whole feature is Enterprise-gated (<c>plugin.sdk</c>)
/// and honors the operator kill-switch.
/// </para>
/// </remarks>
public interface ICustomEndpoint
{
    /// <summary>
    /// Gets the HTTP methods this endpoint handles (e.g. <c>GET</c>, <c>POST</c>). Must contain at
    /// least one method.
    /// </summary>
    IReadOnlyList<string> Methods { get; }

    /// <summary>
    /// Gets the route pattern, rooted under the reserved plugin route prefix the host applies (e.g.
    /// <c>"reports/summary"</c> becomes <c>/plugins/reports/summary</c>). Must not be null/empty.
    /// </summary>
    string Pattern { get; }

    /// <summary>
    /// Gets whether the host should require an authenticated, authorized caller for this route. When
    /// <see langword="true"/> the host applies its standard authorization metadata before invoking
    /// <see cref="HandleAsync"/>. Defaults are host-enforced; plugins cannot bypass authorization.
    /// </summary>
    bool RequiresAuthorization { get; }

    /// <summary>
    /// Handles a request to the contributed route.
    /// </summary>
    /// <param name="request">The adapted, read-only request (method, path, query, headers, body).</param>
    /// <param name="cancellationToken">Cancellation token tied to the request lifetime.</param>
    /// <returns>The response the host writes back to the caller.</returns>
    ValueTask<PluginEndpointResponse> HandleAsync(PluginEndpointRequest request, CancellationToken cancellationToken);
}
