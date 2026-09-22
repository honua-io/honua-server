// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.PackageReview.Domain;
using Honua.Core.Features.Publishing.Content.Abstractions;
using Honua.Core.Features.Publishing.Content.Services;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Services;
using Honua.Geoprocessing;
using Honua.Infrastructure.Models;
using Honua.PackageReview;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.PackageReview;

[Collection("Database")]
[Protocol(TestProtocols.Admin)]
public sealed class PackageReviewEndpointTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new WebAppFixture()
        .ConfigureServices(services =>
        {
            services.RemoveAll<IProcessCatalog>();
            services.AddSingleton<IProcessCatalog>(
                new JobCallableProcessCatalog(new BuiltInProcessCatalog(), "data-management.delete-features"));
            // Map package publication saves through the Studio lifecycle and records operation
            // audit ids; the shared test database carries neither the Studio nor the audit outbox
            // tables, so use the same in-memory stores as StudioPackageEndpointsTests.
            services.RemoveAll<IStudioPackageStore>();
            services.AddSingleton<IStudioPackageStore, InMemoryStudioPackageStore>();
            services.RemoveAll<IContentPublicationStore>();
            services.AddSingleton<IContentPublicationStore, InMemoryContentPublicationStore>();
            services.RemoveAll<IAuditLog>();
            services.AddSingleton<IAuditLog, CapturingAuditLog>();
        });
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.CreateAdminClient();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("POST /api/v1/admin/packages/validate")]
    public async Task ValidatePackage_WithMissingBinding_ReturnsCanonicalBlockedResponse()
    {
        var request = new PackageReviewRequest
        {
            PackageFamily = PackageReviewFamilies.Query,
            PackageId = "pkg-http-blocked",
            Requirements = new PackageReviewRequirements
            {
                DataBindings =
                [
                    new PackageDataBindingRequirement
                    {
                        Id = "source",
                        SourceId = "missing-layer",
                        IsResolved = false,
                        Path = "$.source"
                    }
                ]
            }
        };

        var response = await _client.PostAsync("/api/v1/admin/packages/validate", Serialize(request));

        response.Be200Ok();
        var apiResponse = await ReadResponseAsync(response);
        apiResponse.Success.Should().BeTrue();
        apiResponse.Data.Should().NotBeNull();
        apiResponse.Data!.ContractVersion.Should().Be(PackageReviewContract.Version);
        apiResponse.Data.Status.Should().Be(PackageReviewStatus.Blocked);
        apiResponse.Data.CanExecute.Should().BeFalse();
        apiResponse.Data.CanPublish.Should().BeFalse();
        apiResponse.Data.Findings.Should().ContainSingle(f => f.Code == "missing_data_binding");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("POST /api/v1/admin/packages")]
    public async Task PublishMapPackage_WithoutPackage_ReturnsBadRequest()
    {
        using var content = new StringContent("{\"package\":null}", Encoding.UTF8, "application/json");
        using var response = await _client.PostAsync(
            "/api/v1/admin/packages",
            content);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Map package is required");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("POST /api/v1/admin/packages")]
    public async Task PublishMapPackage_WithCliBuiltPackage_ProposesPublicationWithoutMovingPublishedPointer()
    {
        // honua-server#4906: the body `honua map publish <id> --package @map.json` sends
        // (honua-sdk-js map-package.publish), carrying the honua_map_package.v1 fixture from the
        // sdk-js#1426 replay. It omits nothing the CLI omits and keeps the members the server's
        // MapPackage record does not model (view, layers, widgets, styleRefs[].body).
        var body = $$"""
            {
              "mapId": "honolulu-places",
              "message": "cli",
              "package": {{CliMapPackageJson("\"status\": \"Draft\", \"createdAt\": \"2026-09-15T00:00:00Z\",")}}
            }
            """;

        await using var host = await PublishingHost.StartAsync();
        using var response = await PostPublishAsync(host.Client, body);

        var payload = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Accepted, payload);
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        root.GetProperty("packageId").GetString().Should().Be("honolulu-places");
        root.GetProperty("publicationStatus").GetString().Should().Be("AwaitingApproval");
        root.GetProperty("proposalId").GetString().Should().NotBeNullOrWhiteSpace();
        root.GetProperty("package").GetProperty("status").GetString().Should().Be("Ready");

        var itemId = root.GetProperty("itemId").GetGuid();
        var versionId = root.GetProperty("versionId").GetGuid();
        using var scope = host.Fixture.Services.CreateScope();
        var lifecycle = scope.ServiceProvider.GetRequiredService<IStudioPackageLifecycleService>();
        var pointers = await lifecycle.GetPointersAsync(itemId);
        pointers.Should().NotBeNull();
        pointers!.CurrentVersionId.Should().Be(versionId);
        pointers.PublishedVersionId.Should().BeNull(
            "the admin route proposes publication; only a separate principal's approval may move the published pointer");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("POST /api/v1/admin/packages")]
    public async Task PublishMapPackage_WithoutLifecycleMembers_DefaultsThemAndProposesPublication()
    {
        // status and createdAt are optional in honua_map_package.v1 and in the CLI's HonuaMapPackage.
        await using var host = await PublishingHost.StartAsync();
        using var response = await PostPublishAsync(host.Client, $$"""{"package": {{CliMapPackageJson(string.Empty)}}}""");

        var payload = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Accepted, payload);
        using var document = JsonDocument.Parse(payload);
        document.RootElement.GetProperty("proposalId").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("POST /api/v1/admin/packages")]
    public async Task PublishMapPackage_WithInvalidMember_ReturnsProblemNamingTheMember()
    {
        using var badStatus = await PostPublishAsync(
            _client,
            $$"""{"package": {{CliMapPackageJson("\"status\": \"Shipped\",")}}}""");

        await AssertMemberProblemAsync(badStatus, "$.package.status");

        using var incompleteBinding = await PostPublishAsync(
            _client,
            """{"package": {"mapPackageId": "m", "format": "honua_map_package.v1", "sourceBindings": [{"sourceId": "places"}], "mapSpec": {"version": 8}}}""");

        var errors = await AssertMemberProblemAsync(incompleteBinding, "$.package.sourceBindings[0]");
        errors[0].GetProperty("message").GetString().Should().Contain("protocol").And.Contain("locator")
            .And.NotContain("Honua.", "problem messages must not leak CLR type names");
    }

    private static async Task<HttpResponseMessage> PostPublishAsync(HttpClient client, string body)
    {
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        return await client.PostAsync("/api/v1/admin/packages/", content);
    }

    private static async Task<JsonElement[]> AssertMemberProblemAsync(HttpResponseMessage response, string path)
    {
        var payload = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, payload);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        payload.Should().NotBeNullOrWhiteSpace("a refused package must say why (#4906)");
        using var document = JsonDocument.Parse(payload);
        var errors = document.RootElement.GetProperty("errors").Clone().EnumerateArray().ToArray();
        errors.Should().ContainSingle(error => error.GetProperty("path").GetString() == path, payload);
        return errors;
    }

    private static string CliMapPackageJson(string lifecycleMembers) => $$"""
        {
          "mapPackageId": "honolulu-places",
          "format": "honua_map_package.v1",
          {{lifecycleMembers}}
          "sourceBindings": [],
          "styleRefs": [
            { "styleId": "places-status", "label": "Places status", "body": { "places": { "paint": { "circle-color": "#d93f3f" } } } }
          ],
          "mapSpec": {
            "version": 8,
            "sources": { "places": { "type": "geojson", "data": { "type": "FeatureCollection", "features": [] } } },
            "layers": [ { "id": "places", "type": "circle", "source": "places", "paint": { "circle-radius": 12 } } ]
          },
          "initialView": { "bbox": [-158.3, 20.9, -157.4, 21.7], "center": [-157.8583, 21.3069], "zoom": 5, "crs": "EPSG:4326" },
          "view": { "center": [-157.8583, 21.3069], "zoom": 5, "crs": "EPSG:4326" },
          "layers": [],
          "widgets": [],
          "controls": [],
          "interactions": []
        }
        """;

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("POST /api/v1/admin/packages/preview")]
    public async Task PreviewPackage_WithReadyPackage_ReturnsReadOnlyPreviewPlan()
    {
        var request = new PackageReviewRequest
        {
            PackageFamily = PackageReviewFamilies.Query,
            PackageId = "pkg-http-preview",
            IncludePreviewPlan = false,
            Requirements = new PackageReviewRequirements
            {
                DataBindings =
                [
                    new PackageDataBindingRequirement
                    {
                        Id = "source",
                        SourceId = "layers/parks",
                        IsResolved = true
                    }
                ],
                Capabilities =
                [
                    new PackageCapabilityRequirement
                    {
                        Capability = "features.query",
                        Supported = true
                    }
                ]
            }
        };

        var response = await _client.PostAsync("/api/v1/admin/packages/preview", Serialize(request));

        response.Be200Ok();
        var apiResponse = await ReadResponseAsync(response);
        apiResponse.Data.Should().NotBeNull();
        apiResponse.Data!.Status.Should().Be(PackageReviewStatus.Ready);
        apiResponse.Data.PreviewPlan.Should().NotBeNull();
        apiResponse.Data.PreviewPlan!.MayMutatePublishedState.Should().BeFalse();
        apiResponse.Data.PreviewPlan.Operations.Should().OnlyContain(operation => !operation.MayMutatePublishedState);
        apiResponse.Data.PreviewPlan.Operations[0].InputRefs.Should().Contain("layers/parks");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("POST /api/v1/admin/packages/preview")]
    public async Task PreviewPackage_WithDuplicateAnalysisStep_ReturnsBlockedFindingWithoutPreviewPlan()
    {
        var request = CreateAnalysisPlanRequest(
            "pkg-invalid-analysis",
            """
            {
              "planId": "plan-duplicate",
              "intentId": "intent-duplicate",
              "steps": [
                {
                  "stepId": "duplicate",
                  "kind": "Geoprocess",
                  "processId": "geometry.buffer"
                },
                {
                  "stepId": "duplicate",
                  "kind": "Geoprocess",
                  "processId": "geometry.buffer"
                }
              ],
              "outputs": [ "FeatureLayer" ]
            }
            """);

        var response = await _client.PostAsync("/api/v1/admin/packages/preview", Serialize(request));

        response.Be200Ok();
        var apiResponse = await ReadResponseAsync(response);
        apiResponse.Data.Should().NotBeNull();
        apiResponse.Data!.Status.Should().Be(PackageReviewStatus.Blocked);
        apiResponse.Data.PreviewPlan.Should().BeNull();
        apiResponse.Data.Findings.Should().ContainSingle(f =>
            f.Code == "invalid_analysis_plan_payload" &&
            f.Evidence.Any(e => e.Actual != null && e.Actual.Contains("Duplicate step identifier", StringComparison.Ordinal)));
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("POST /api/v1/admin/packages/validate")]
    public async Task ValidatePackage_WithAdminDestructiveAnalysisPlan_DoesNotRequireApproval()
    {
        var request = CreateAnalysisPlanRequest(
            "pkg-admin-destructive-analysis",
            """
            {
              "planId": "plan-delete",
              "intentId": "intent-delete",
              "steps": [
                {
                  "stepId": "delete",
                  "kind": "Geoprocess",
                  "processId": "data-management.delete-features",
                  "inputs": {
                    "layerId": "42",
                    "where": "status = 'retired'"
                  }
                }
              ],
              "outputs": [ "Scalar" ]
            }
            """);

        var response = await _client.PostAsync("/api/v1/admin/packages/validate", Serialize(request));

        response.Be200Ok();
        var apiResponse = await ReadResponseAsync(response);
        apiResponse.Data.Should().NotBeNull();
        apiResponse.Data!.Status.Should().Be(PackageReviewStatus.Ready);
        apiResponse.Data.RequiresApproval.Should().BeFalse();
        apiResponse.Data.Findings.Should().NotContain(f => f.Code == "approval_required");
    }

    private static PackageReviewRequest CreateAnalysisPlanRequest(string packageId, string payloadJson)
    {
        using var document = JsonDocument.Parse(payloadJson);
        return new PackageReviewRequest
        {
            PackageFamily = PackageReviewFamilies.AnalysisPlan,
            PackageId = packageId,
            PackagePayload = document.RootElement.Clone()
        };
    }

    private static StringContent Serialize(PackageReviewRequest request)
        => new(
            JsonSerializer.Serialize(request, PackageReviewJsonContext.Default.PackageReviewRequest),
            Encoding.UTF8,
            "application/json");

    private static async Task<ApiResponse<PackageReviewResponse>> ReadResponseAsync(HttpResponseMessage response)
    {
        var payload = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(
            payload,
            PackageReviewJsonContext.Default.ApiResponsePackageReviewResponse);
        result.Should().NotBeNull($"response payload was: {payload}");
        return result!;
    }

    private sealed class CapturingAuditLog : IAuditLog
    {
        public Task<string?> RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>("audit-test");
    }

    /// <summary>
    /// A host that can hold a governed publication proposal: the durable proposal store and
    /// operation gateway are registered only on a Redis-connected, cache-entitled host, as on the
    /// released image.
    /// </summary>
    private sealed class PublishingHost : IAsyncDisposable
    {
        private readonly RedisFixture _redis;

        private PublishingHost(RedisFixture redis, WebAppFixture fixture)
        {
            _redis = redis;
            Fixture = fixture;
            Client = fixture.CreateAdminClient();
        }

        public WebAppFixture Fixture { get; }

        public HttpClient Client { get; }

        public static async Task<PublishingHost> StartAsync()
        {
            var redis = new RedisFixture();
            await redis.InitializeAsync();
            var fixture = new WebAppFixture()
                .ConfigureWebHost(builder =>
                {
                    builder.UseSetting("ConnectionStrings:redis", redis.ConnectionString);
                    builder.UseSetting("Licensing:DevGrantEdition", "Pro");
                })
                .ConfigureServices(services =>
                {
                    services.RemoveAll<IStudioPackageStore>();
                    services.AddSingleton<IStudioPackageStore, InMemoryStudioPackageStore>();
                    services.RemoveAll<IContentPublicationStore>();
                    services.AddSingleton<IContentPublicationStore, InMemoryContentPublicationStore>();
                    services.RemoveAll<IAuditLog>();
                    services.AddSingleton<IAuditLog, CapturingAuditLog>();
                });
            await fixture.InitializeAsync();
            return new PublishingHost(redis, fixture);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Fixture.DisposeAsync();
            await _redis.DisposeAsync();
        }
    }
}
