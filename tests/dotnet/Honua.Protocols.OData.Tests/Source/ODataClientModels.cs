// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.OData.Client;
using Microsoft.Spatial;

namespace Honua.Server.Tests.Features.Protocols.OData;

internal static class ODataTestClient
{
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

internal sealed class ODataTestHttpClientFactory(HttpClient client) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => client;
}

[Key("Id")]
internal sealed class ODataLayer
{
    public int Id { get; init; }
    public string? Name { get; init; }
    public string? Description { get; init; }
}

[Key("ObjectId")]
internal sealed class ODataFeature
{
    public long ObjectId { get; init; }
    public int LayerId { get; init; }
    public Geography? Geometry { get; init; }
    public string? Attributes { get; init; }
}
