// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Exports;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Operations.Policy;
using Honua.Core.Features.Operations.Services;
using Honua.Infrastructure.Progress;
using Honua.Server.Features.Admin.Exports;
using Honua.Server.Features.Admin.TileOperations;
using Honua.Server.Features.Operations;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Infrastructure;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Admin;

public sealed partial class ExportEndpointTests
{
    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/exports/{exportId}")]
    [Endpoint("GET /api/v1/admin/exports")]
    public async Task GetExport_AfterTwoVersions_DescribesBothWithSignedInUrls()
    {
        var first = await PublishAsync(IdentityField);
        var second = await PublishAsync(IdentityField);

        var export = await GetExportAsync();

        Text(export, "status").Should().Be(ExportResponse.Ready);
        Text(export, "currentVersion").Should().Be(second.Version);
        Text(export, "collectionUrl").Should().EndWith($"/api/v1/exports/{_exportId}/{second.Version}/collection.json");
        Text(export, "notPublicReason").Should().NotBeNullOrEmpty();
        export.GetProperty("versions").EnumerateArray().Select(version => Text(version, "version"))
            .Should().Equal(first.Version, second.Version);
        export.GetProperty("versions")[1].GetProperty("artifacts").EnumerateArray().Select(artifact => Text(artifact, "kind"))
            .Should().BeEquivalentTo("collection", "geoparquet", "pmtiles");

        using var list = JsonDocument.Parse(await _client.GetStringAsync($"{ExportsRoute}?limit=50&offset=0"));
        list.RootElement.GetProperty("data").GetProperty("items").EnumerateArray()
            .Should().Contain(item => Text(item, "id") == _exportId);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/exports")]
    [Endpoint("DELETE /api/v1/admin/exports/{exportId}")]
    public async Task AgentCaller_ExportCreateAndDelete_RequireHumanApprovalAndMapScopes()
    {
        AgentApprovalRule.IsGated("admin.exports.create").Should().BeTrue();
        AgentApprovalRule.IsGated("admin.exports.delete").Should().BeTrue();
        OperationScopeMapping.TryResolve(new OperationRequest { OperationId = "admin.exports.list" }, out var listOp).Should().BeTrue();
        listOp.Should().Be(OperatorOperation.Read);
        OperationScopeMapping.TryResolve(new OperationRequest { OperationId = "admin.exports.create" }, out var createOp).Should().BeTrue();
        createOp.Should().Be(OperatorOperation.Create);
        OperationScopeMapping.TryResolve(new OperationRequest { OperationId = "admin.exports.delete" }, out var deleteOp).Should().BeTrue();
        deleteOp.Should().Be(OperatorOperation.Delete);

        var pdp = _fixture.GetOptionalService<IOperationPolicyDecisionPoint>()!;
        var create = AdminOperateOperationCatalog.Descriptors.Single(d => d.OperationId == "admin.exports.create");
        var delete = AdminOperateOperationCatalog.Descriptors.Single(d => d.OperationId == "admin.exports.delete");
        var agent = new OperationPolicyContext
        {
            PrincipalId = "agent-export-test",
            Roles = [AgentApprovalRule.AgentRole, OperationPolicyBuiltInRules.FullAdministratorRole]
        };

        (await pdp.EvaluateAsync(create, new OperationRequest { OperationId = create.OperationId }, agent))
            .Kind.Should().Be(PolicyDecisionKind.RequireApproval);
        (await pdp.EvaluateAsync(delete, new OperationRequest { OperationId = delete.OperationId }, agent))
            .Kind.Should().Be(PolicyDecisionKind.RequireApproval);
    }

    [IntegrationTest]
    [Endpoint("DELETE /api/v1/admin/exports/{exportId}")]
    public async Task DeleteExport_WritesDurableMarkerThatPreservesSourceIdentity()
    {
        var published = await PublishAsync(IdentityField);

        using var deleted = await _client.DeleteAsync($"{ExportsRoute}/{_exportId}");
        deleted.StatusCode.Should().Be(HttpStatusCode.OK);

        var markerKey = published.PointerKey[..^ExportStore.PointerFileName.Length] + ExportStore.UnpublishedFileName;
        var marker = await ReadJsonAsync<ExportUnpublishedMarker>(markerKey);
        marker.ExportId.Should().Be(_exportId);
        marker.ServiceId.Should().NotBeNullOrWhiteSpace();
        marker.LayerId.Should().NotBeNullOrWhiteSpace();

        var store = ExportStore.Create(Storage, _fixture.GetOptionalService<IOptions<CloudStorageOptions>>()!.Value, _exportId);
        var etagBefore = await store.ReadUnpublishedETagAsync(CancellationToken.None);
        etagBefore.Should().NotBeNullOrWhiteSpace();

        // A job that started before DELETE (null initial tag) is fenced from activating after DELETE.
        var midJobFence = () => ExportUnpublishGuard.RequireNotUnpublishedAsync(
            store, new ExportUnpublishFence(InitialETag: null), CancellationToken.None);
        await midJobFence.Should().ThrowAsync<ExportException>().WithMessage("*unpublished while the job was running*");

        // A job created before DELETE is fenced even if retried after DELETE with the current marker tag.
        var staleRetryFence = () => ExportUnpublishGuard.RequireNotUnpublishedAsync(
            store, new ExportUnpublishFence(etagBefore, marker.UnpublishedAt.AddSeconds(-1)), CancellationToken.None);
        await staleRetryFence.Should().ThrowAsync<ExportException>().WithMessage("*unpublished after the job was created*");

        // A republish started after DELETE sees the current tag, activates, and clears the marker.
        await ExportUnpublishGuard.RequireNotUnpublishedAsync(
            store, new ExportUnpublishFence(etagBefore, marker.UnpublishedAt.AddSeconds(1)), CancellationToken.None);
        await PublishAsync(IdentityField);
        (await store.ReadUnpublishedETagAsync(CancellationToken.None)).Should().BeNull();
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/exports")]
    [Endpoint("DELETE /api/v1/admin/exports/{exportId}")]
    public async Task CreateAndDeleteExport_WhileJobIsRunning_ReturnConflict()
    {
        var progressStore = _fixture.GetOptionalService<IUniversalProgressStore>()!;
        var jobId = $"export-active-{Guid.NewGuid():N}";
        await progressStore.SetProgressAsync(
            jobId,
            TileOperationProgress.CreateInitial(jobId, TileCacheExecutionSpecBuilder.ExportOperation, WebAppFixture.TestServiceId, WebAppFixture.TestLayerId, "WebMercatorQuad") with
            {
                ExportId = _exportId,
                Status = Honua.Core.Features.Infrastructure.Domain.OperationStatus.Processing
            },
            TimeSpan.FromMinutes(5),
            CancellationToken.None);

        using var createConflict = await _client.PostAsJsonAsync(ExportsRoute, PublishBody(IdentityField));
        using var deleteConflict = await _client.DeleteAsync($"{ExportsRoute}/{_exportId}");

        createConflict.StatusCode.Should().Be(HttpStatusCode.Conflict);
        deleteConflict.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/exports")]
    public async Task ListExports_RespectsLimitAndOffset()
    {
        await PublishAsync(IdentityField);
        using var secondCreate = await _client.PostAsJsonAsync(ExportsRoute, new
        {
            source = new { kind = "layer", serviceId = WebAppFixture.TestServiceId, layerId = WebAppFixture.TestLayerId },
            exportId = $"{_exportId}-b",
            identityField = IdentityField,
            minZoom = 0,
            maxZoom = 0
        });
        secondCreate.StatusCode.Should().Be(HttpStatusCode.Accepted);
        using var secondBody = JsonDocument.Parse(await secondCreate.Content.ReadAsStringAsync());
        await WaitForJobAsync(Text(secondBody.RootElement.GetProperty("data"), "jobId"));

        using var firstPage = JsonDocument.Parse(await _client.GetStringAsync($"{ExportsRoute}?limit=1&offset=0"));
        using var secondPage = JsonDocument.Parse(await _client.GetStringAsync($"{ExportsRoute}?limit=1&offset=1"));
        var firstItems = firstPage.RootElement.GetProperty("data").GetProperty("items");
        var secondItems = secondPage.RootElement.GetProperty("data").GetProperty("items");
        firstItems.GetArrayLength().Should().Be(1);
        secondItems.GetArrayLength().Should().Be(1);
        Text(firstItems[0], "id").Should().NotBe(Text(secondItems[0], "id"));
    }

    [IntegrationTest]
    [Endpoint("DELETE /api/v1/admin/exports/{exportId}")]
    [Endpoint("GET /api/v1/exports/{exportId}/current.json")]
    public async Task DeleteExport_Published_StopsServingAndKeepsItsVersions()
    {
        var published = await PublishAsync(IdentityField);

        using var deleted = await _client.DeleteAsync($"{ExportsRoute}/{_exportId}");

        deleted.StatusCode.Should().Be(HttpStatusCode.OK);
        var export = await GetExportAsync();
        Text(export, "status").Should().Be(ExportResponse.Unpublished);
        export.GetProperty("versions").EnumerateArray().Select(version => Text(version, "version")).Should().Equal(published.Version);
        using var current = await _client.GetAsync($"/api/v1/exports/{_exportId}/current.json");
        current.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("DELETE /api/v1/admin/exports/{exportId}")]
    [Endpoint("POST /api/v1/admin/tile-operations/jobs/{jobId}/retry")]
    [Endpoint("POST /api/v1/admin/jobs/{jobId}/retry")]
    [Endpoint("GET /api/v1/exports/{exportId}/current.json")]
    public async Task RetryFailedExportJob_AfterUnpublish_ReturnsConflictAndStaysUnpublished()
    {
        await PublishAsync(IdentityField);
        var failedJobId = await SeedFailedExportJobAsync();

        using var deleted = await _client.DeleteAsync($"{ExportsRoute}/{_exportId}");
        deleted.StatusCode.Should().Be(HttpStatusCode.OK);

        using var tileRetry = await _client.PostAsync($"{JobsRoute}/{failedJobId}/retry", content: null);
        tileRetry.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await tileRetry.Content.ReadAsStringAsync()).Should().Contain("/api/v1/admin/exports");

        using var consoleRetry = await _client.PostAsync($"/api/v1/admin/jobs/{failedJobId}/retry", content: null);
        consoleRetry.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await consoleRetry.Content.ReadAsStringAsync()).Should().Contain("/api/v1/admin/exports");

        var store = ExportStore.Create(Storage, _fixture.GetOptionalService<IOptions<CloudStorageOptions>>()!.Value, _exportId);
        var marker = await store.ReadUnpublishedAsync(CancellationToken.None);
        marker.Should().NotBeNull();
        (await store.ReadPointerAsync(CancellationToken.None)).Should().BeNull();

        using var current = await _client.GetAsync($"/api/v1/exports/{_exportId}/current.json");
        current.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private async Task<string> SeedFailedExportJobAsync()
    {
        var jobStore = _fixture.GetOptionalService<IExecutionJobStore>()!;
        var progressStore = _fixture.GetOptionalService<IUniversalProgressStore>()!;
        var batchOptions = _fixture.GetOptionalService<IOptions<TileCacheBatchOptions>>()?.Value ?? new TileCacheBatchOptions();
        var failedJobId = $"tile-failed-export-{Guid.NewGuid():N}";
        var request = new TileOperationStartRequest
        {
            Operation = TileCacheExecutionSpecBuilder.ExportOperation,
            ServiceId = WebAppFixture.TestServiceId,
            LayerId = WebAppFixture.TestLayerId,
            TileMatrixSetId = "WebMercatorQuad",
            ExportId = _exportId,
            IdentityField = IdentityField,
            MinZoom = 0,
            MaxZoom = 0
        };
        var now = DateTimeOffset.UtcNow;
        var created = await jobStore.TryCreateAsync(new ExecutionJobRecord
        {
            OperationId = failedJobId,
            Status = ExecutionJobStatus.Failed,
            CreatedAt = now,
            UpdatedAt = now,
            CurrentPhase = "Failed",
            Spec = TileCacheExecutionSpecBuilder.Build(request, schemaName: null, batchOptions)
        });
        created.Should().BeTrue();
        await progressStore.SetProgressAsync(
            failedJobId,
            TileOperationProgress.CreateInitial(failedJobId, request.Operation, request.ServiceId, request.LayerId, request.TileMatrixSetId) with
            {
                ExportId = _exportId,
                Status = Honua.Core.Features.Infrastructure.Domain.OperationStatus.Failed,
                ErrorMessage = "Simulated failure"
            },
            TimeSpan.FromMinutes(5),
            CancellationToken.None);
        return failedJobId;
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/exports/{exportId}")]
    [Endpoint("DELETE /api/v1/admin/exports/{exportId}")]
    public async Task GetAndDeleteExport_Unknown_ReturnNotFound()
    {
        using var get = await _client.GetAsync("/api/v1/admin/exports/never-exported");
        using var delete = await _client.DeleteAsync("/api/v1/admin/exports/never-exported");

        get.StatusCode.Should().Be(HttpStatusCode.NotFound);
        delete.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/exports")]
    public async Task CreateExport_Started_ReturnsRunningExportWithItsJob()
    {
        using var response = await _client.PostAsJsonAsync(ExportsRoute, PublishBody(IdentityField));

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        response.Headers.Location!.OriginalString.Should().EndWith($"/api/v1/admin/exports/{_exportId}");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var export = body.RootElement.GetProperty("data");
        Text(export, "status").Should().Be(ExportResponse.Running);
        Text(export, "jobId").Should().NotBeNullOrEmpty();
        await WaitForJobAsync(Text(export, "jobId"));
    }

    private async Task<JsonElement> GetExportAsync()
    {
        using var response = await _client.GetAsync($"{ExportsRoute}/{_exportId}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("data").Clone();
    }

    private async Task WaitForJobAsync(string jobId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            using var json = JsonDocument.Parse(await _client.GetStringAsync($"{JobsRoute}/{jobId}"));
            if (Property(json.RootElement, "status").GetInt32() >= 2)
            {
                return;
            }

            await Task.Delay(200);
        }
    }

    private static string Text(JsonElement element, string name) => element.GetProperty(name).GetString()!;
}
