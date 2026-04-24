// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain;
using Honua.Core.Features.Metadata.Schema;
using Honua.Server.Features.Admin.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>
/// Integration tests for manifest approval workflow endpoints.
/// </summary>
[Protocol(TestProtocols.Admin)]
[Collection("Database")]
public sealed class ManifestApprovalEndpointsTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture;

    public ManifestApprovalEndpointsTests()
    {
        _fixture = new WebAppFixture().ConfigureWebHost(builder =>
        {
            builder.ConfigureAppConfiguration((_, configBuilder) =>
            {
                configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ManifestApproval:Enabled"] = "true",
                    ["ManifestApproval:DefaultTimeoutMinutes"] = "60"
                });
            });
        });
    }

    public Task InitializeAsync() => _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("POST /api/v1/admin/manifest/apply")]
    public async Task ManifestApply_WithApprovalRequired_Returns202Accepted()
    {
        var client = _fixture.CreateAdminClient();
        var resource = CreateLayerResource();

        var applyRequest = new ManifestApplyRequest
        {
            Resources = new[] { resource },
            DryRun = false,
            Prune = false,
            ApprovalRequired = true,
            RequestedBy = "test-user",
            RequestedReason = "Production deploy"
        };

        var response = await client.PostAsync(
            "/api/v1/admin/manifest/apply",
            JsonContent.Create(applyRequest, MetadataResourceJsonContext.Default.ManifestApplyRequest));

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var payload = await response.Content.ReadAsStringAsync();
        var apiResponse = JsonSerializer.Deserialize(
            payload,
            ManifestApprovalJsonContext.Default.ApiResponseManifestPendingChangeResponse);

        apiResponse.Should().NotBeNull();
        apiResponse!.Success.Should().BeTrue();
        apiResponse.Data.Should().NotBeNull();
        apiResponse.Data!.Status.Should().Be("pending");
        apiResponse.Data.RequestedBy.Should().Be("test-user");
        apiResponse.Data.ResourceCount.Should().Be(1);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /api/v1/admin/manifest/pending")]
    public async Task ListPending_ReturnsPendingChanges()
    {
        var client = _fixture.CreateAdminClient();
        var resource = CreateLayerResource();

        // Queue a change for approval
        var applyRequest = new ManifestApplyRequest
        {
            Resources = new[] { resource },
            DryRun = false,
            Prune = false,
            ApprovalRequired = true,
            RequestedBy = "list-test-user"
        };

        await client.PostAsync(
            "/api/v1/admin/manifest/apply",
            JsonContent.Create(applyRequest, MetadataResourceJsonContext.Default.ManifestApplyRequest));

        // List pending changes
        var listResponse = await client.GetAsync("/api/v1/admin/manifest/pending/");
        listResponse.Be200Ok();

        var payload = await listResponse.Content.ReadAsStringAsync();
        var apiResponse = JsonSerializer.Deserialize(
            payload,
            ManifestApprovalJsonContext.Default.ApiResponseManifestPendingChangeResponseArray);

        apiResponse.Should().NotBeNull();
        apiResponse!.Success.Should().BeTrue();
        apiResponse.Data.Should().NotBeNull();
        apiResponse.Data!.Should().Contain(p => p.RequestedBy == "list-test-user");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /api/v1/admin/manifest/pending/{id}")]
    public async Task GetPending_ReturnsSinglePendingChange()
    {
        var client = _fixture.CreateAdminClient();
        var resource = CreateLayerResource();

        var applyRequest = new ManifestApplyRequest
        {
            Resources = new[] { resource },
            DryRun = false,
            Prune = false,
            ApprovalRequired = true,
            RequestedBy = "get-test-user"
        };

        var queueResponse = await client.PostAsync(
            "/api/v1/admin/manifest/apply",
            JsonContent.Create(applyRequest, MetadataResourceJsonContext.Default.ManifestApplyRequest));

        queueResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var queuePayload = await queueResponse.Content.ReadAsStringAsync();
        var queueResult = JsonSerializer.Deserialize(
            queuePayload,
            ManifestApprovalJsonContext.Default.ApiResponseManifestPendingChangeResponse);

        var pendingId = queueResult!.Data!.PendingId;

        var getResponse = await client.GetAsync($"/api/v1/admin/manifest/pending/{pendingId}");
        getResponse.Be200Ok();

        var getPayload = await getResponse.Content.ReadAsStringAsync();
        var getResult = JsonSerializer.Deserialize(
            getPayload,
            ManifestApprovalJsonContext.Default.ApiResponseManifestPendingChangeResponse);

        getResult.Should().NotBeNull();
        getResult!.Success.Should().BeTrue();
        getResult.Data.Should().NotBeNull();
        getResult.Data!.PendingId.Should().Be(pendingId);
        getResult.Data.RequestedBy.Should().Be("get-test-user");
        getResult.Data.Status.Should().Be("pending");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("POST /api/v1/admin/manifest/pending/{id}/approve")]
    public async Task Approve_AppliesQueuedManifest()
    {
        var client = _fixture.CreateAdminClient();
        var resource = CreateLayerResource();

        // Queue for approval
        var applyRequest = new ManifestApplyRequest
        {
            Resources = new[] { resource },
            DryRun = false,
            Prune = false,
            ApprovalRequired = true,
            RequestedBy = "approve-test-user"
        };

        var queueResponse = await client.PostAsync(
            "/api/v1/admin/manifest/apply",
            JsonContent.Create(applyRequest, MetadataResourceJsonContext.Default.ManifestApplyRequest));

        queueResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var queuePayload = await queueResponse.Content.ReadAsStringAsync();
        var queueResult = JsonSerializer.Deserialize(
            queuePayload,
            ManifestApprovalJsonContext.Default.ApiResponseManifestPendingChangeResponse);

        var pendingId = queueResult!.Data!.PendingId;

        // Approve the pending change
        var approveRequest = new ManifestApproveRequest
        {
            ApprovedBy = "admin-reviewer",
            Reason = "Looks good"
        };

        var approveResponse = await client.PostAsync(
            $"/api/v1/admin/manifest/pending/{pendingId}/approve",
            JsonContent.Create(approveRequest, ManifestApprovalJsonContext.Default.ManifestApproveRequest));

        approveResponse.Be200Ok();

        var approvePayload = await approveResponse.Content.ReadAsStringAsync();
        var applyResult = JsonSerializer.Deserialize(
            approvePayload,
            ManifestApprovalJsonContext.Default.ApiResponseManifestApplyResult);

        applyResult.Should().NotBeNull();
        applyResult!.Success.Should().BeTrue();
        applyResult.Data.Should().NotBeNull();
        applyResult.Data!.Summary.Created.Should().BeGreaterOrEqualTo(1);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("POST /api/v1/admin/manifest/pending/{id}/reject")]
    public async Task Reject_RecordsReasonAndDoesNotApply()
    {
        var client = _fixture.CreateAdminClient();
        var resource = CreateLayerResource();

        // Queue for approval
        var applyRequest = new ManifestApplyRequest
        {
            Resources = new[] { resource },
            DryRun = false,
            Prune = false,
            ApprovalRequired = true,
            RequestedBy = "reject-test-user"
        };

        var queueResponse = await client.PostAsync(
            "/api/v1/admin/manifest/apply",
            JsonContent.Create(applyRequest, MetadataResourceJsonContext.Default.ManifestApplyRequest));

        var queuePayload = await queueResponse.Content.ReadAsStringAsync();
        var queueResult = JsonSerializer.Deserialize(
            queuePayload,
            ManifestApprovalJsonContext.Default.ApiResponseManifestPendingChangeResponse);

        var pendingId = queueResult!.Data!.PendingId;

        // Reject the pending change
        var rejectRequest = new ManifestRejectRequest
        {
            RejectedBy = "admin-reviewer",
            Reason = "Not ready for production"
        };

        var rejectResponse = await client.PostAsync(
            $"/api/v1/admin/manifest/pending/{pendingId}/reject",
            JsonContent.Create(rejectRequest, ManifestApprovalJsonContext.Default.ManifestRejectRequest));

        rejectResponse.Be200Ok();

        var rejectPayload = await rejectResponse.Content.ReadAsStringAsync();
        var rejectResult = JsonSerializer.Deserialize(
            rejectPayload,
            ManifestApprovalJsonContext.Default.ApiResponseManifestPendingChangeResponse);

        rejectResult.Should().NotBeNull();
        rejectResult!.Data.Should().NotBeNull();
        rejectResult.Data!.Status.Should().Be("rejected");
        rejectResult.Data.DecisionBy.Should().Be("admin-reviewer");
        rejectResult.Data.DecisionReason.Should().Be("Not ready for production");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /api/v1/admin/manifest/pending/history")]
    public async Task History_ReturnsAllChanges()
    {
        var client = _fixture.CreateAdminClient();
        var resource = CreateLayerResource();

        // Queue and reject a change
        var applyRequest = new ManifestApplyRequest
        {
            Resources = new[] { resource },
            DryRun = false,
            Prune = false,
            ApprovalRequired = true,
            RequestedBy = "history-test-user"
        };

        var queueResponse = await client.PostAsync(
            "/api/v1/admin/manifest/apply",
            JsonContent.Create(applyRequest, MetadataResourceJsonContext.Default.ManifestApplyRequest));

        var queuePayload = await queueResponse.Content.ReadAsStringAsync();
        var queueResult = JsonSerializer.Deserialize(
            queuePayload,
            ManifestApprovalJsonContext.Default.ApiResponseManifestPendingChangeResponse);

        await client.PostAsync(
            $"/api/v1/admin/manifest/pending/{queueResult!.Data!.PendingId}/reject",
            JsonContent.Create(new ManifestRejectRequest { RejectedBy = "reviewer", Reason = "test" },
                ManifestApprovalJsonContext.Default.ManifestRejectRequest));

        // Query history
        var historyResponse = await client.GetAsync("/api/v1/admin/manifest/pending/history");
        historyResponse.Be200Ok();

        var historyPayload = await historyResponse.Content.ReadAsStringAsync();
        var historyResult = JsonSerializer.Deserialize(
            historyPayload,
            ManifestApprovalJsonContext.Default.ApiResponseManifestPendingChangeResponseArray);

        historyResult.Should().NotBeNull();
        historyResult!.Data.Should().NotBeNull();
        historyResult.Data!.Should().Contain(p => p.RequestedBy == "history-test-user" && p.Status == "rejected");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /api/v1/admin/manifest/pending/history")]
    public async Task History_ReturnsMoreThanDefaultStoreLimit()
    {
        using var scope = _fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IManifestPendingChangeStore>();
        var snapshot = JsonSerializer.SerializeToElement(
            new ManifestApplyRequest
            {
                Resources = new[] { CreateLayerResource("history-over-limit") },
                DryRun = false,
                Prune = false
            },
            MetadataResourceJsonContext.Default.ManifestApplyRequest);

        for (var i = 0; i < 205; i++)
        {
            await store.CreateAsync(new ManifestPendingChange
            {
                PendingId = Guid.NewGuid(),
                ManifestSnapshot = snapshot,
                ManifestHash = $"history-{i:D3}",
                Status = ManifestApprovalStatus.Pending,
                RequestedBy = $"history-user-{i:D3}",
                ResourceCount = 1,
                CreatedAt = DateTimeOffset.UtcNow.AddSeconds(-i)
            });
        }

        var client = _fixture.CreateAdminClient();
        var historyResponse = await client.GetAsync("/api/v1/admin/manifest/pending/history");
        historyResponse.Be200Ok();

        var historyPayload = await historyResponse.Content.ReadAsStringAsync();
        var historyResult = JsonSerializer.Deserialize(
            historyPayload,
            ManifestApprovalJsonContext.Default.ApiResponseManifestPendingChangeResponseArray);

        historyResult.Should().NotBeNull();
        historyResult!.Data.Should().NotBeNull();
        historyResult.Data!.Length.Should().BeGreaterOrEqualTo(205);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("POST /api/v1/admin/manifest/pending/{id}/approve")]
    public async Task Approve_WhenApplyFails_RestoresPendingStatus()
    {
        var failingFixture = new WebAppFixture()
            .ConfigureWebHost(builder =>
            {
                builder.ConfigureAppConfiguration((_, configBuilder) =>
                {
                    configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ManifestApproval:Enabled"] = "true",
                        ["ManifestApproval:DefaultTimeoutMinutes"] = "60"
                    });
                });
            })
            .ReplaceService<IMetadataCompiler>(new ThrowingMetadataCompiler());

        await failingFixture.InitializeAsync();
        try
        {
            var client = failingFixture.CreateAdminClient();
            var applyRequest = new ManifestApplyRequest
            {
                Resources = new[] { CreateLayerResource("approve-failure") },
                DryRun = false,
                Prune = false,
                ApprovalRequired = true,
                RequestedBy = "approve-failure-test"
            };

            var queueResponse = await client.PostAsync(
                "/api/v1/admin/manifest/apply",
                JsonContent.Create(applyRequest, MetadataResourceJsonContext.Default.ManifestApplyRequest));
            queueResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);

            var queuePayload = await queueResponse.Content.ReadAsStringAsync();
            var queueResult = JsonSerializer.Deserialize(
                queuePayload,
                ManifestApprovalJsonContext.Default.ApiResponseManifestPendingChangeResponse);
            var pendingId = queueResult!.Data!.PendingId;

            var approveResponse = await client.PostAsync(
                $"/api/v1/admin/manifest/pending/{pendingId}/approve",
                JsonContent.Create(
                    new ManifestApproveRequest { ApprovedBy = "admin-reviewer", Reason = "force-failure" },
                    ManifestApprovalJsonContext.Default.ManifestApproveRequest));

            approveResponse.StatusCode.Should().Be(HttpStatusCode.InternalServerError);

            var pendingResponse = await client.GetAsync($"/api/v1/admin/manifest/pending/{pendingId}");
            pendingResponse.Be200Ok();

            var pendingPayload = await pendingResponse.Content.ReadAsStringAsync();
            var pendingResult = JsonSerializer.Deserialize(
                pendingPayload,
                ManifestApprovalJsonContext.Default.ApiResponseManifestPendingChangeResponse);

            pendingResult.Should().NotBeNull();
            pendingResult!.Data.Should().NotBeNull();
            pendingResult.Data!.Status.Should().Be("pending");
            pendingResult.Data.DecisionBy.Should().BeNull();
            pendingResult.Data.DecisionReason.Should().BeNull();
            pendingResult.Data.DecidedAt.Should().BeNull();
        }
        finally
        {
            await failingFixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("POST /api/v1/admin/manifest/apply")]
    public async Task ManifestApply_WithApprovalRequired_WhenDisabled_Returns403()
    {
        // Use a fresh fixture without approval enabled
        var disabledFixture = new WebAppFixture().ConfigureWebHost(builder =>
        {
            builder.ConfigureAppConfiguration((_, configBuilder) =>
            {
                configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ManifestApproval:Enabled"] = "false"
                });
            });
        });

        await disabledFixture.InitializeAsync();
        try
        {
            var client = disabledFixture.CreateAdminClient();
            var resource = CreateLayerResource();

            var applyRequest = new ManifestApplyRequest
            {
                Resources = new[] { resource },
                DryRun = false,
                Prune = false,
                ApprovalRequired = true
            };

            var response = await client.PostAsync(
                "/api/v1/admin/manifest/apply",
                JsonContent.Create(applyRequest, MetadataResourceJsonContext.Default.ManifestApplyRequest));

            response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }
        finally
        {
            await disabledFixture.DisposeAsync();
        }
    }

    private static MetadataResource CreateLayerResource(string? name = null)
    {
        var resourceName = name ?? $"approval-layer-{Guid.NewGuid():N}";
        var resourceSpec = JsonSerializer.SerializeToElement(new
        {
            tableName = "parcels",
            schemaName = "public",
            geometryType = "Polygon",
            srid = 4326
        });

        return new MetadataResource
        {
            ApiVersion = MetadataSchemaRegistry.CurrentVersion,
            Kind = MetadataResourceKinds.Layer,
            Metadata = new ResourceMetadata
            {
                Name = resourceName,
                Namespace = "default"
            },
            Spec = resourceSpec
        };
    }

    private sealed class ThrowingMetadataCompiler : IMetadataCompiler
    {
        public Task<MetadataCompilationResult> CompileAsync(MetadataResource resource, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("compiler boom");
    }
}
