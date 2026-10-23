// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http;
using System.Text;
using FluentAssertions;
using Honua.TestKit;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using NSubstitute;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Import;

[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Import)]
public sealed class ImportUrlEndpointTests : IAsyncLifetime
{
    private WebAppFixture _fixture = null!;
    private HttpClient _client = null!;
    private readonly IOperationInvoker _invoker = Substitute.For<IOperationInvoker>();

    public async Task InitializeAsync()
    {
        var geoJsonContent = """
        {
            "type": "FeatureCollection",
            "features": [
                {
                    "type": "Feature",
                    "geometry": {
                        "type": "Point",
                        "coordinates": [-122.4194, 37.7749]
                    },
                    "properties": {
                        "name": "Remote Test Point"
                    }
                }
            ]
        }
        """;

        _invoker.SubmitAsync(Arg.Any<OperationRequest>(), Arg.Any<OperationPolicyContext>(), Arg.Any<CancellationToken>())
            .Returns(call => new OperationHandle
            {
                OperationInstanceId = "endpoint-owned-operation",
                OperationId = call.ArgAt<OperationRequest>(0).OperationId,
                CorrelationId = "endpoint-correlation",
                AuditId = "endpoint-durable-audit",
                Status = OperationHandleStatus.Queued,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                JobId = "endpoint-owned-job",
                Result = new OperationResultSummary
                {
                    Summary = "Import queued",
                    Details = new Dictionary<string, string>
                    {
                        ["statusCode"] = "202",
                        ["response"] = "{\"jobId\":\"endpoint-owned-job\"}",
                    },
                },
            });
        _fixture = new WebAppFixture()
            .ConfigureServices(services =>
            {
                services.AddSingleton(_invoker);
                services.AddSingleton<IHttpClientFactory>(
                    new StubHttpClientFactory(
                        "https://s3.amazonaws.com/sample-bucket/test.geojson",
                        "application/json",
                        geoJsonContent));
            });
        await _fixture.InitializeAsync();
        _client = _fixture.Client;
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTheory]
    [InlineData("/api/v1/admin/import/upload-url")]
    [InlineData("/api/v1/admin/imports")]
    [Endpoint("POST /api/v1/admin/import/upload-url")]
    [Endpoint("POST /api/v1/admin/imports")]
    public async Task UploadUrl_WithIdempotencyKey_RoutesThroughCanonicalOperation(string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent("""{"sourceUrl":"https://s3.amazonaws.com/sample-bucket/test.geojson","tableName":"canonical_route"}"""),
        };
        request.Headers.Add("Idempotency-Key", "route-regression");
        using var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await response.Content.ReadAsStringAsync()).Should().Contain("endpoint-owned-job");
        response.Headers.GetValues("X-Honua-Operation-Instance-Id").Should().ContainSingle().Which.Should().Be("endpoint-owned-operation");
        await _invoker.Received(1).SubmitAsync(
            Arg.Is<OperationRequest>(operation => operation.OperationId == "admin.import.upload-url" && operation.Parameters["tableName"] == "canonical_route"),
            Arg.Is<OperationPolicyContext>(policy => policy.PrincipalId != null && policy.IdempotencyKey != "route-regression" && policy.IdempotencyRequestHash != null),
            Arg.Any<CancellationToken>());
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/preview-url")]
    public async Task PreviewUrl_WithSupportedPublicS3Source_ReturnsPreview()
    {
        using var response = await _client.PostAsync(
            "/api/v1/admin/import/preview-url",
            JsonContent("""
            {
              "sourceUrl": "https://s3.amazonaws.com/sample-bucket/test.geojson"
            }
            """));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("GeoJson");
        content.Should().Contain("Remote Test Point");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/upload-url")]
    public async Task UploadUrl_WithSupportedPublicS3Source_ImportsData()
    {
        using var response = await _client.PostAsync(
            "/api/v1/admin/import/upload-url",
            JsonContent("""
            {
              "sourceUrl": "https://s3.amazonaws.com/sample-bucket/test.geojson",
              "tableName": "remote_url_import_table",
              "overwriteExisting": true
            }
            """));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("remote_url_import_table");
        content.Should().Contain("GeoJson");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/imports")]
    public async Task CompatibilityPluralImportRoute_WithSupportedPublicS3Source_ImportsData()
    {
        using var response = await _client.PostAsync(
            "/api/v1/admin/imports",
            JsonContent("""
            {
              "sourceUrl": "https://s3.amazonaws.com/sample-bucket/test.geojson",
              "tableName": "plural_remote_url_import_table",
              "overwriteExisting": true
            }
            """));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("plural_remote_url_import_table");
        content.Should().Contain("GeoJson");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/imports/jobs")]
    public async Task CompatibilityPluralImportJobs_ReturnsActiveJobs()
    {
        using var response = await _client.GetAsync("/api/v1/admin/imports/jobs");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/imports/jobs/{jobId}")]
    public async Task CompatibilityPluralImportJobStatus_UnknownJob_ReturnsNotFound()
    {
        using var response = await _client.GetAsync("/api/v1/admin/imports/jobs/unknown-job");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/imports/jobs/{jobId}/cancel")]
    public async Task CompatibilityPluralImportJobCancel_UnknownJob_ReturnsNotFound()
    {
        using var response = await _client.PostAsync("/api/v1/admin/imports/jobs/unknown-job/cancel", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private static StringContent JsonContent(string json)
        => new(json, Encoding.UTF8, "application/json");

    private sealed class StubHttpClientFactory(string expectedUrl, string contentType, string body) : IHttpClientFactory, IDisposable
    {
        private readonly HttpClient _client = new(new StubHttpMessageHandler(expectedUrl, contentType, body));

        public HttpClient CreateClient(string name) => _client;

        public void Dispose()
        {
            _client.Dispose();
        }
    }

    private sealed class StubHttpMessageHandler(string expectedUrl, string contentType, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.RequestUri.Should().NotBeNull();
            request.RequestUri!.ToString().Should().Be(expectedUrl);

            // Ownership transfers to the HttpClient pipeline, which disposes the response.
            var response = new Honua.TestKit.CallerOwnedHttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, contentType)
            };
            return Task.FromResult<HttpResponseMessage>(response);
        }
    }
}
