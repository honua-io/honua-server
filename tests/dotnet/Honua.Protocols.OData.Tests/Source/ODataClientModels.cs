// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.OData.Client;
using Microsoft.Spatial;

namespace Honua.Server.Tests.Features.Protocols.OData;

/// <summary>
/// Shared Microsoft.OData.Client entity model and context helpers used by
/// OData test classes (integration and certification). Hoisting these out of
/// the original test class lets the certification suite reuse the same
/// declarations rather than redeclaring them per the DRY constraint.
/// </summary>
internal static class ODataTestClient
{
    /// <summary>
    /// Creates a <see cref="DataServiceContext"/> wired to the test
    /// <see cref="HttpClient"/> via <see cref="ODataTestHttpClientFactory"/>.
    /// </summary>
    public static DataServiceContext CreateContext(HttpClient client)
    {
        var serviceRoot = new Uri(client.BaseAddress!, "odata/");
        var context = new DataServiceContext(serviceRoot, ODataProtocolVersion.V4)
        {
            HttpClientFactory = new ODataTestHttpClientFactory(client),
        };
        context.Format.UseJson();
        return context;
    }
}

/// <summary>
/// Routes Microsoft.OData.Client HTTP traffic through the in-memory test
/// host instead of opening real sockets.
/// </summary>
internal sealed class ODataTestHttpClientFactory(HttpClient client) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => client;
}

/// <summary>
/// OData entity type for the <c>Layers</c> collection. Properties match the
/// EDM schema exposed by <c>/odata</c> for the YAML seed used by the OData
/// test classes.
/// </summary>
[Key("Id")]
internal sealed class ODataLayer
{
    public int Id { get; init; }
    public string? Name { get; init; }
    public string? Description { get; init; }
}

/// <summary>
/// OData entity type for the <c>Features</c> collection. The certification
/// lane reads <see cref="ObjectId"/> and <see cref="LayerId"/> only; the
/// other properties are kept for parity with the integration suite.
/// </summary>
[Key("ObjectId")]
internal sealed class ODataFeature
{
    public long ObjectId { get; init; }
    public int LayerId { get; init; }
    public Geography? Geometry { get; init; }
    public string? Attributes { get; init; }
}
