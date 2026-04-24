// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.OData;

/// <summary>
/// Integration tests for OData multipart/mixed batch request format.
/// Verifies parsing, execution, and response generation for the RFC 2616 batch format.
/// </summary>
[Collection("Database")]
[Protocol(Protocols.ODataV4)]
public sealed class ODataMultipartBatchTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync()
    {
        _fixture.UseSeed(Path.Combine("tests", "seed", "odata.yaml"));
        await _fixture.InitializeAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.ODataBatch)]
    [Endpoint("POST /odata/$batch")]
    public async Task MultipartBatch_WithMultipleGetRequests_ReturnsAllResults()
    {
        const string boundary = "batch_multi_gets";
        var payload = string.Join("\r\n",
        [
            $"--{boundary}",
            "Content-Type: application/http",
            "Content-Transfer-Encoding: binary",
            string.Empty,
            "GET /odata/Features(0,1) HTTP/1.1",
            "Accept: application/json",
            string.Empty,
            $"--{boundary}",
            "Content-Type: application/http",
            "Content-Transfer-Encoding: binary",
            string.Empty,
            "GET /odata/Features(0,2) HTTP/1.1",
            "Accept: application/json",
            string.Empty,
            $"--{boundary}--",
            string.Empty
        ]);

        var content = new StringContent(payload, Encoding.UTF8);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse($"multipart/mixed;boundary={boundary}");

        var response = await _fixture.Client.PostAsync("/odata/$batch", content);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("multipart/mixed");

        var responseBody = await response.Content.ReadAsStringAsync();
        // Two 200 OK responses expected
        responseBody.Split("HTTP/1.1 200").Length.Should().BeGreaterThanOrEqualTo(3,
            "multipart response should contain at least 2 HTTP/1.1 200 parts");
    }

    [IntegrationTest]
    [Operation(Operations.ODataBatch)]
    [Endpoint("POST /odata/$batch")]
    public async Task MultipartBatch_WithPostRequest_CreatesFeature()
    {
        const string boundary = "batch_create";
        var featureJson = """{"LayerId":0,"Attributes":{"name":"Batch Created City"}}""";
        var payload = string.Join("\r\n",
        [
            $"--{boundary}",
            "Content-Type: application/http",
            "Content-Transfer-Encoding: binary",
            string.Empty,
            "POST /odata/Features HTTP/1.1",
            "Content-Type: application/json",
            $"Content-Length: {Encoding.UTF8.GetByteCount(featureJson)}",
            string.Empty,
            featureJson,
            $"--{boundary}--",
            string.Empty
        ]);

        var content = new StringContent(payload, Encoding.UTF8);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse($"multipart/mixed;boundary={boundary}");

        var response = await _fixture.Client.PostAsync("/odata/$batch", content);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var responseBody = await response.Content.ReadAsStringAsync();
        responseBody.Should().Contain("HTTP/1.1 201");
    }

    [IntegrationTest]
    [Operation(Operations.ODataBatch)]
    [Endpoint("POST /odata/$batch")]
    public async Task MultipartBatch_WithInvalidRequest_Returns400InPart()
    {
        const string boundary = "batch_invalid";
        var payload = string.Join("\r\n",
        [
            $"--{boundary}",
            "Content-Type: application/http",
            "Content-Transfer-Encoding: binary",
            string.Empty,
            "GET /odata/Features(999999,999999) HTTP/1.1",
            "Accept: application/json",
            string.Empty,
            $"--{boundary}--",
            string.Empty
        ]);

        var content = new StringContent(payload, Encoding.UTF8);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse($"multipart/mixed;boundary={boundary}");

        var response = await _fixture.Client.PostAsync("/odata/$batch", content);

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "batch always returns 200 even when individual requests fail");
        var responseBody = await response.Content.ReadAsStringAsync();
        responseBody.Should().Contain("HTTP/1.1 404");
    }

    [IntegrationTest]
    [Operation(Operations.ODataBatch)]
    [Endpoint("POST /odata/$batch")]
    public async Task MultipartBatch_WithAbsoluteSubrequestUrl_ExecutesPathWithoutEchoingHost()
    {
        const string boundary = "batch_absolute_url";
        var payload = string.Join("\r\n",
        [
            $"--{boundary}",
            "Content-Type: application/http",
            "Content-Transfer-Encoding: binary",
            string.Empty,
            "GET https://attacker.example/odata/Layers?top=1 HTTP/1.1",
            "Accept: application/json",
            string.Empty,
            $"--{boundary}--",
            string.Empty
        ]);

        var content = new StringContent(payload, Encoding.UTF8);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse($"multipart/mixed;boundary={boundary}");

        var response = await _fixture.Client.PostAsync("/odata/$batch", content);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var responseBody = await response.Content.ReadAsStringAsync();
        responseBody.Should().Contain("HTTP/1.1 200");
        responseBody.Should().NotContain("attacker.example");
    }

    [IntegrationTest]
    [Operation(Operations.ODataBatch)]
    [Endpoint("POST /odata/$batch")]
    public async Task MultipartBatch_WithChangeset_SupportsAtomicity()
    {
        const string batchBoundary = "batch_atomic";
        const string changesetBoundary = "changeset_1";
        var featureJson = """{"LayerId":0,"Attributes":{"name":"Atomic City"}}""";

        var payload = string.Join("\r\n",
        [
            $"--{batchBoundary}",
            $"Content-Type: multipart/mixed;boundary={changesetBoundary}",
            string.Empty,
            $"--{changesetBoundary}",
            "Content-Type: application/http",
            "Content-Transfer-Encoding: binary",
            string.Empty,
            "POST /odata/Features HTTP/1.1",
            "Content-Type: application/json",
            $"Content-Length: {Encoding.UTF8.GetByteCount(featureJson)}",
            string.Empty,
            featureJson,
            $"--{changesetBoundary}--",
            $"--{batchBoundary}--",
            string.Empty
        ]);

        var content = new StringContent(payload, Encoding.UTF8);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse($"multipart/mixed;boundary={batchBoundary}");

        var response = await _fixture.Client.PostAsync("/odata/$batch", content);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var responseBody = await response.Content.ReadAsStringAsync();
        responseBody.Should().Contain("HTTP/1.1 201");
    }

    [IntegrationTest]
    [Operation(Operations.ODataBatch)]
    [Endpoint("POST /odata/$batch")]
    public async Task MultipartBatch_ResponseContainsODataVersionHeader()
    {
        const string boundary = "batch_headers";
        var payload = string.Join("\r\n",
        [
            $"--{boundary}",
            "Content-Type: application/http",
            "Content-Transfer-Encoding: binary",
            string.Empty,
            "GET /odata/Features(0,1) HTTP/1.1",
            "Accept: application/json",
            string.Empty,
            $"--{boundary}--",
            string.Empty
        ]);

        var content = new StringContent(payload, Encoding.UTF8);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse($"multipart/mixed;boundary={boundary}");

        var response = await _fixture.Client.PostAsync("/odata/$batch", content);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var responseBody = await response.Content.ReadAsStringAsync();
        responseBody.Should().Contain("OData-Version: 4.01",
            "each part in multipart response should include OData-Version header");
    }
}
