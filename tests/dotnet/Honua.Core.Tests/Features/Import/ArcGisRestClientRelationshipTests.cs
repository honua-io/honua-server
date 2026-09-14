// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using Honua.Core.Features.Migration.Services;
using Honua.TestKit;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Core.Tests.Features.Import;

public sealed class ArcGisRestClientRelationshipTests
{
    [Theory]
    [InlineData("Feature Layer", "esriRelRoleOrigin", 1)]
    [InlineData("Table", "esriRelRoleDestination", 0)]
    public async Task GetLayerInfoAsync_CapturesRelationshipIdentityKeysAndOwnership(
        string resourceType, string role, int relatedTableId)
    {
        var metadata = $$"""
            {"id":1,"name":"Summary","type":"{{resourceType}}","relationships":[
              {"id":0,"name":"groupBySummary","relatedTableId":{{relatedTableId}},
               "cardinality":"esriRelCardinalityOneToMany","role":"{{role}}",
               "keyField":"Join_ID","composite":true}
            ]}
            """;
        using var handler = new RelationshipSourceHandler(metadata);
        using var http = new HttpClient(handler);
        var client = new ArcGisRestClient(http, NullLogger<ArcGisRestClient>.Instance,
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }));

        var layer = await client.GetLayerInfoAsync("https://example.com/arcgis/rest/services/Summary/FeatureServer",
            1, 5, 0, CancellationToken.None);

        var relationship = layer.Relationships.Should().ContainSingle().Which!;
        relationship.Id.Should().Be(0);
        relationship.RelatedTableId.Should().Be(relatedTableId);
        relationship.Name.Should().Be("groupBySummary");
        relationship.KeyField.Should().Be("Join_ID");
        relationship.Role.Should().Be(role);
        relationship.Cardinality.Should().Be("esriRelCardinalityOneToMany");
        relationship.Composite.Should().BeTrue();
    }

    [Theory]
    [InlineData("{}", 0)]
    [InlineData("{\"relationships\":null}", 0)]
    [InlineData("{\"relationships\":[]}", 0)]
    [InlineData("{\"relationships\":[null,{}]}", 2)]
    public async Task GetLayerInfoAsync_DoesNotDiscardIncompleteDeclarations(string metadata, int expected)
    {
        using var handler = new RelationshipSourceHandler(metadata);
        using var http = new HttpClient(handler);
        var client = new ArcGisRestClient(http, NullLogger<ArcGisRestClient>.Instance,
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }));

        var layer = await client.GetLayerInfoAsync("https://example.com/arcgis/rest/services/Summary/FeatureServer",
            1, 5, 0, CancellationToken.None);

        layer.Relationships.Should().HaveCount(expected);
        if (expected > 0)
        {
            layer.Relationships[0].Should().BeNull();
            layer.Relationships[1]!.Id.Should().BeNull();
            layer.Relationships[1]!.RelatedTableId.Should().BeNull();
        }
    }

    private sealed class RelationshipSourceHandler(string metadata) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult<HttpResponseMessage>(new CallerOwnedHttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(request.RequestUri!.AbsolutePath.EndsWith("/query", StringComparison.Ordinal)
                    ? "{\"count\":2}" : metadata, Encoding.UTF8, "application/json")
            });
    }
}
