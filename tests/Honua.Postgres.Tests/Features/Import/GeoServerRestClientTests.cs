// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using FluentAssertions;
using Honua.Postgres.Features.Import;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Postgres.Tests.Features.Import;

public sealed class GeoServerRestClientTests
{
    [Fact]
    public async Task DiscoverServiceAsync_WithVersionXml_ParsesGeoServerVersionMetadata()
    {
        using var httpClient = new HttpClient(new StubGeoServerHandler());
        var client = new GeoServerRestClient(httpClient, NullLogger<GeoServerRestClient>.Instance);

        var result = await client.DiscoverServiceAsync(
            "https://example.com/geoserver/rest",
            username: null,
            password: null,
            includeCompatibilityAnalysis: false,
            includeStyleContent: false,
            timeoutSeconds: 5,
            maxRetryAttempts: 0,
            CancellationToken.None);

        result.Version.Should().Be("2.28.0");
        result.BuildTimestamp.Should().Be("13-Oct-2025 05:02");
        result.GitRevision.Should().Be("16064e8d72a06ac36b373d8cd7cf328bde2be7cd");
    }

    private sealed class StubGeoServerHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            var (contentType, payload) = path switch
            {
                "/geoserver/rest/about/version.xml" => ("application/xml", """
                    <about>
                      <resource name="GeoServer">
                        <Build-Timestamp>13-Oct-2025 05:02</Build-Timestamp>
                        <Version>2.28.0</Version>
                        <Git-Revision>16064e8d72a06ac36b373d8cd7cf328bde2be7cd</Git-Revision>
                      </resource>
                      <resource name="GeoTools">
                        <Build-Timestamp>12-Oct-2025 00:03</Build-Timestamp>
                        <Version>34.0</Version>
                        <Git-Revision>b07b3543dba73763574d6e1749dfd72e4b14fe90</Git-Revision>
                      </resource>
                    </about>
                    """),
                "/geoserver/rest/settings.json" => ("application/json", """{"global":{}}"""),
                "/geoserver/rest/workspaces.json" => ("application/json", """{"workspaces":{"workspace":""}}"""),
                "/geoserver/rest/layergroups.json" => ("application/json", """{"layerGroups":{"layerGroup":""}}"""),
                "/geoserver/rest/styles.json" => ("application/json", """{"styles":{"style":""}}"""),
                _ => throw new InvalidOperationException($"Unexpected GeoServer request path: {path}")
            };

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, contentType)
            });
        }
    }
}
