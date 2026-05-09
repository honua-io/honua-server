// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace Honua.Server.Tests.Features.CloudDemo;

[Collection("Database")]
[Protocol(TestProtocols.Streaming)]
public sealed class CloudDemoEndpointsTests : IAsyncLifetime
{
    private const string ResetToken = "test-reset-token";
    private const string WriteToken = "test-write-token";

    private readonly WebAppFixture _fixture;
    private HttpClient _client = null!;

    public CloudDemoEndpointsTests()
    {
        _fixture = new WebAppFixture()
            .ConfigureWebHost(builder =>
            {
                builder.ConfigureAppConfiguration((_, configBuilder) =>
                {
                    configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["CloudDemoServices:Enabled"] = "true",
                        ["CloudDemoServices:AllowWrites"] = "true",
                        ["CloudDemoServices:ResetToken"] = ResetToken,
                        ["CloudDemoServices:WriteToken"] = WriteToken
                    });
                });
            });
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.Client;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _fixture.DisposeAsync();
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("POST /api/v1/cloud-demo/reset")]
    public async Task Reset_WithoutToken_ReturnsUnauthorized()
    {
        using var response = await _client.PostAsync("/api/v1/cloud-demo/reset", new ByteArrayContent([]));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("cloud_demo_reset_token_invalid");
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("POST /api/v1/cloud-demo/reset")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task Reset_WithValidToken_SeedsFeatureServerContractAliases()
    {
        using var resetRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/cloud-demo/reset");
        resetRequest.Headers.Add("X-Honua-Demo-Reset-Token", ResetToken);

        using var resetResponse = await _client.SendAsync(resetRequest);
        resetResponse.Be200Ok();

        using var resetJson = JsonDocument.Parse(await resetResponse.Content.ReadAsStringAsync());
        resetJson.RootElement.GetProperty("state").GetString().Should().Be("seeded-clean");
        resetJson.RootElement.GetProperty("serviceId").GetString().Should().Be("field-inspections-demo-writable");
        resetJson.RootElement.GetProperty("layerId").GetInt32().Should().Be(0);

        using var queryResponse = await _client.GetAsync(
            "/rest/services/natural-earth/FeatureServer/0/query?where=1%3D1&outFields=*&f=json");
        queryResponse.Be200Ok();

        using var queryJson = JsonDocument.Parse(await queryResponse.Content.ReadAsStringAsync());
        queryJson.RootElement.GetProperty("features").GetArrayLength().Should().BeGreaterThan(0);
    }

    [IntegrationTest]
    [Operation(Operations.Streaming)]
    [Endpoint("GET /api/v1/realtime/incidents/sse")]
    public async Task IncidentSse_Once_ReturnsStatusSnapshotAndHeartbeat()
    {
        using var response = await _client.GetAsync(
            "/api/v1/realtime/incidents/sse?once=true&cursor=cloud-demo-cursor-0001&stale=true",
            HttpCompletionOption.ResponseHeadersRead);

        response.Be200Ok();
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/event-stream");

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("event: status");
        body.Should().Contain("\"status\":\"stale\"");
        body.Should().Contain("event: snapshot");
        body.Should().Contain("\"cursor\":\"cloud-demo-replay-0002\"");
        body.Should().Contain("event: heartbeat");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task StoryCollection_AfterReset_ReturnsSeededOgcFeatures()
    {
        using var resetRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/cloud-demo/reset");
        resetRequest.Headers.Add("X-Honua-Demo-Reset-Token", ResetToken);
        using var resetResponse = await _client.SendAsync(resetRequest);
        resetResponse.Be200Ok();

        using var response = await _client.GetAsync("/ogc/features/collections/story-25d-assets/items?limit=1");
        response.Be200Ok();

        using var content = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        content.RootElement.GetProperty("features").GetArrayLength().Should().BeGreaterThan(0);
    }

    [IntegrationTest]
    [Operation(Operations.ApplyEdits)]
    [Operation(Operations.Append)]
    [Operation(Operations.Calculate)]
    [Operation(Operations.AddAttachment)]
    [Operation(Operations.UpdateAttachment)]
    [Operation(Operations.DeleteAttachments)]
    [Operation(Operations.CreateReplica)]
    [Operation(Operations.SynchronizeReplica)]
    [Operation(Operations.UnRegisterReplica)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/applyEdits")]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/append")]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/createReplica")]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/synchronizeReplica")]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/unRegisterReplica")]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/append")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/calculate")]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/calculate")]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/{featureId}/addAttachment")]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/{featureId}/updateAttachment")]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/{featureId}/deleteAttachments")]
    public async Task WritableFeatureGuard_WithoutWriteToken_ReturnsUnauthorizedForWritableOperations()
    {
        var cases = new (HttpMethod Method, string Path)[]
        {
            (HttpMethod.Post, "/rest/services/field-inspections-demo-writable/FeatureServer/applyEdits"),
            (HttpMethod.Post, "/rest/services/field-inspections-demo-writable/FeatureServer/append"),
            (HttpMethod.Post, "/rest/services/field-inspections-demo-writable/FeatureServer/createReplica"),
            (HttpMethod.Post, "/rest/services/field-inspections-demo-writable/FeatureServer/synchronizeReplica"),
            (HttpMethod.Post, "/rest/services/field-inspections-demo-writable/FeatureServer/unRegisterReplica"),
            (HttpMethod.Post, "/rest/services/field-inspections-demo-writable/FeatureServer/0/applyEdits"),
            (HttpMethod.Post, "/rest/services/field-inspections-demo-writable/FeatureServer/0/append"),
            (HttpMethod.Get, "/rest/services/field-inspections-demo-writable/FeatureServer/0/calculate"),
            (HttpMethod.Post, "/rest/services/field-inspections-demo-writable/FeatureServer/0/calculate"),
            (HttpMethod.Post, "/rest/services/field-inspections-demo-writable/FeatureServer/0/1/addAttachment"),
            (HttpMethod.Post, "/rest/services/field-inspections-demo-writable/FeatureServer/0/1/updateAttachment"),
            (HttpMethod.Post, "/rest/services/field-inspections-demo-writable/FeatureServer/0/1/deleteAttachments")
        };

        foreach (var (method, path) in cases)
        {
            using var request = new HttpRequestMessage(method, path);
            if (method != HttpMethod.Get)
            {
                request.Content = new FormUrlEncodedContent(new Dictionary<string, string>());
            }

            using var response = await _client.SendAsync(request);

            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, path);
            var body = await response.Content.ReadAsStringAsync();
            body.Should().Contain("cloud_demo_write_token_invalid", path);
        }
    }
}
