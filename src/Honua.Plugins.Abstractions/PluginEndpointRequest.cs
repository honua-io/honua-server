// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;

namespace Honua.Plugins.Abstractions;

/// <summary>
/// Read-only, ASP.NET-free view of an HTTP request passed to an <see cref="ICustomEndpoint"/>
/// handler. The host populates this from the incoming <c>HttpContext</c> so plugin authors write
/// handlers against the dependency-minimal SDK contract rather than the ASP.NET request types.
/// </summary>
/// <param name="Method">The HTTP method (uppercased, e.g. <c>GET</c>).</param>
/// <param name="Path">The request path as routed (e.g. <c>/plugins/reports/summary</c>).</param>
/// <param name="RouteValues">Matched route values for the endpoint pattern.</param>
/// <param name="Query">The parsed query-string values (first value per key).</param>
/// <param name="Headers">A safe subset of request headers exposed to the plugin (first value per key).</param>
/// <param name="Body">The raw request body bytes, when present.</param>
/// <param name="Actor">The authenticated actor performing the request, when available.</param>
public sealed record PluginEndpointRequest(
    string Method,
    string Path,
    ImmutableDictionary<string, string?> RouteValues,
    ImmutableDictionary<string, string?> Query,
    ImmutableDictionary<string, string?> Headers,
    ReadOnlyMemory<byte> Body,
    string? Actor);
