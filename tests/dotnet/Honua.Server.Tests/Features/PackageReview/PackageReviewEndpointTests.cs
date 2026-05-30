// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.PackageReview.Domain;
using Honua.Infrastructure.Models;
using Honua.PackageReview;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;

namespace Honua.Server.Tests.Features.PackageReview;

[Collection("Database")]
[Protocol(TestProtocols.Admin)]
public sealed class PackageReviewEndpointTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
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
}
