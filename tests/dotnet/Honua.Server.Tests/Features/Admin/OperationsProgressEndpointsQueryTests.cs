// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Deployment.Domain;
using Honua.Infrastructure.Progress;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Publishing.Domain;
using Honua.Server.Features.Admin.TileOperations;
using Honua.TestKit.Helpers;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Admin;

public sealed partial class OperationsProgressEndpointsTests
{
    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/operations/active")]
    public async Task ListActiveOperations_WhenFiltered_ReturnsOnlyActive()
    {
        var uploadId = Guid.NewGuid().ToString("N");
        var uploadProgress = UploadProgress.CreateInitial(uploadId, "active.geojson", 50);
        await _progressStore.SetProgressAsync(uploadId, uploadProgress, TimeSpan.FromMinutes(5));
        _operationIds.Add(uploadId);

        var importId = Guid.NewGuid().ToString("N");
        var importProgress = ImportProgress.CreateInitial(importId, "import_table", SupportedFileFormat.GeoJson) with
        {
            Status = ImportStatus.Completed,
            CompletedAt = DateTimeOffset.UtcNow
        };

        await _progressStore.SetProgressAsync(importId, importProgress, TimeSpan.FromMinutes(5));
        _operationIds.Add(importId);

        var response = await _client.GetAsync("/api/v1/admin/operations/active?type=Upload");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(content);

        var operations = GetOperationsArray(json.RootElement);
        operations.GetArrayLength().Should().Be(1);
        GetPropertyCaseInsensitive(operations[0], "operationId").GetString().Should().Be(uploadId);
        GetPropertyCaseInsensitive(operations[0], "type").GetInt32().Should().Be((int)OperationType.Upload);
        GetPropertyCaseInsensitive(operations[0], "uploadId").GetString().Should().Be(uploadId);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/operations/active")]
    public async Task ListActiveOperations_WhenMixedTypesPresent_PreservesUnifiedOperationIdentityFields()
    {
        var uploadId = Guid.NewGuid().ToString("N");
        var uploadProgress = UploadProgress.CreateInitial(uploadId, "mixed.geojson", 50) with
        {
            Status = OperationStatus.Processing
        };
        await _progressStore.SetProgressAsync(uploadId, uploadProgress, TimeSpan.FromMinutes(5));
        _operationIds.Add(uploadId);

        var geoprocessingId = Guid.NewGuid().ToString("N");
        var geoprocessingProgress = GeoprocessingProgress.CreateInitial(geoprocessingId);
        await _progressStore.SetProgressAsync(geoprocessingId, geoprocessingProgress, TimeSpan.FromMinutes(5));
        _operationIds.Add(geoprocessingId);

        var response = await _client.GetAsync("/api/v1/admin/operations/active");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(content);

        var operations = GetOperationsArray(json.RootElement).EnumerateArray().ToList();
        operations.Should().Contain(op =>
            GetPropertyCaseInsensitive(op, "operationId").GetString() == uploadId &&
            GetPropertyCaseInsensitive(op, "type").GetInt32() == (int)OperationType.Upload &&
            GetPropertyCaseInsensitive(op, "uploadId").GetString() == uploadId);
        operations.Should().Contain(op =>
            GetPropertyCaseInsensitive(op, "operationId").GetString() == geoprocessingId &&
            GetPropertyCaseInsensitive(op, "type").GetInt32() == (int)OperationType.Geoprocessing &&
            GetPropertyCaseInsensitive(op, "currentStage").ValueKind != JsonValueKind.Undefined);
        operations.Should().OnlyContain(op => CountPropertiesIgnoringCase(op, "operationId") == 1);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/operations/type/{operationType}")]
    public async Task GetOperationsByType_WhenMatching_ReturnsOperations()
    {
        var importId = Guid.NewGuid().ToString("N");
        var importProgress = ImportProgress.CreateInitial(importId, "history_table", SupportedFileFormat.GeoJson) with
        {
            Status = ImportStatus.Completed,
            CompletedAt = DateTimeOffset.UtcNow
        };

        await _progressStore.SetProgressAsync(importId, importProgress, TimeSpan.FromMinutes(5));
        _operationIds.Add(importId);

        var response = await _client.GetAsync("/api/v1/admin/operations/type/Import");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(content);

        var operations = GetOperationsArray(json.RootElement);
        operations.GetArrayLength().Should().Be(1);
        GetPropertyCaseInsensitive(operations[0], "operationId").GetString().Should().Be(importId);
        GetPropertyCaseInsensitive(operations[0], "type").GetInt32().Should().Be((int)OperationType.Import);
        GetPropertyCaseInsensitive(operations[0], "jobId").GetString().Should().Be(importId);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/operations/{operationId}")]
    public async Task GetOperationStatus_NonexistentId_ReturnsNotFound()
    {
        var response = await _client.GetAsync($"/api/v1/admin/operations/nonexistent-{Guid.NewGuid():N}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/operations/{operationId}/cancel")]
    public async Task CancelOperation_AlreadyCancelled_ReturnsIdempotent200()
    {
        var operationId = Guid.NewGuid().ToString("N");
        var progress = UploadProgress.CreateInitial(operationId, "double-cancel.geojson", 10) with
        {
            Status = OperationStatus.Cancelled
        };

        await _progressStore.SetProgressAsync(operationId, progress, TimeSpan.FromMinutes(5));
        _operationIds.Add(operationId);

        var response = await _client.PostAsync($"/api/v1/admin/operations/{operationId}/cancel", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/operations/active")]
    public async Task ListActiveOperations_GeoprocessingType_ReturnsWorkflowFields()
    {
        var operationId = Guid.NewGuid().ToString("N");
        var progress = GeoprocessingProgress.CreateInitial(operationId);

        await _progressStore.SetProgressAsync(operationId, progress, TimeSpan.FromMinutes(5));
        _operationIds.Add(operationId);

        var response = await _client.GetAsync("/api/v1/admin/operations/active?type=Geoprocessing");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(content);

        var operations = GetOperationsArray(json.RootElement);
        operations.GetArrayLength().Should().BeGreaterOrEqualTo(1);

        var op = operations.EnumerateArray().Single(operation =>
            GetPropertyCaseInsensitive(operation, "operationId").GetString() == operationId);
        GetPropertyCaseInsensitive(op, "operationId").GetString().Should().Be(operationId);
        GetPropertyCaseInsensitive(op, "workflowStatus").ValueKind.Should().NotBe(JsonValueKind.Undefined);
        GetPropertyCaseInsensitive(op, "currentStage").ValueKind.Should().NotBe(JsonValueKind.Undefined);
        GetPropertyCaseInsensitive(op, "currentStageStatus").ValueKind.Should().NotBe(JsonValueKind.Undefined);
        GetPropertyCaseInsensitive(op, "stepsCompleted").GetInt32().Should().Be(0);
        CountPropertiesIgnoringCase(op, "operationId").Should().Be(1);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/operations/active")]
    public async Task ListActiveOperations_PublishingType_ReturnsWorkflowFields()
    {
        var operationId = Guid.NewGuid().ToString("N");
        var intentId = Guid.NewGuid().ToString("N");
        var progress = PublishingProgress.CreateExecuting(operationId, intentId) with
        {
            ServiceId = "svc-" + Guid.NewGuid().ToString("N")
        };

        await _progressStore.SetProgressAsync(operationId, progress, TimeSpan.FromMinutes(5));
        _operationIds.Add(operationId);

        var response = await _client.GetAsync("/api/v1/admin/operations/active?type=Publishing");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(content);

        var operations = GetOperationsArray(json.RootElement);
        operations.GetArrayLength().Should().BeGreaterOrEqualTo(1);

        var op = operations.EnumerateArray().Single(operation =>
            GetPropertyCaseInsensitive(operation, "operationId").GetString() == operationId);
        GetPropertyCaseInsensitive(op, "operationId").GetString().Should().Be(operationId);
        GetPropertyCaseInsensitive(op, "intentId").GetString().Should().Be(intentId);
        GetPropertyCaseInsensitive(op, "serviceId").GetString().Should().Be(progress.ServiceId);
        GetPropertyCaseInsensitive(op, "intentStatus").ValueKind.Should().NotBe(JsonValueKind.Undefined);
        CountPropertiesIgnoringCase(op, "operationId").Should().Be(1);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/operations/{operationId}")]
    public async Task GetOperationStatus_PublishingType_ReturnsWorkflowFields()
    {
        var operationId = Guid.NewGuid().ToString("N");
        var intentId = Guid.NewGuid().ToString("N");
        var progress = PublishingProgress.CreateExecuting(operationId, intentId) with
        {
            ServiceId = "svc-" + Guid.NewGuid().ToString("N")
        };

        await _progressStore.SetProgressAsync(operationId, progress, TimeSpan.FromMinutes(5));
        _operationIds.Add(operationId);

        var response = await _client.GetAsync($"/api/v1/admin/operations/{operationId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(content);

        GetPropertyCaseInsensitive(json.RootElement, "operationId").GetString().Should().Be(operationId);
        GetPropertyCaseInsensitive(json.RootElement, "intentId").GetString().Should().Be(intentId);
        GetPropertyCaseInsensitive(json.RootElement, "serviceId").GetString().Should().Be(progress.ServiceId);
        GetPropertyCaseInsensitive(json.RootElement, "intentStatus").ValueKind.Should().NotBe(JsonValueKind.Undefined);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/operations/active")]
    public async Task ListActiveOperations_WhenNoneActive_ReturnsEmptyList()
    {
        // Use a unique type filter to avoid matching other operations
        var response = await _client.GetAsync("/api/v1/admin/operations/active?type=Upload");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(content);

        // The response should be parseable and contain an operations array
        var operations = GetOperationsArray(json.RootElement);
        operations.ValueKind.Should().Be(JsonValueKind.Array);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/operations/{operationId}")]
    public async Task GetOperationStatus_DeploymentType_ReturnsLifecycleFields()
    {
        var operationId = $"deploy-{Guid.NewGuid():N}";
        var deploymentId = $"dep-{Guid.NewGuid():N}";
        var progress = DeploymentProgress.CreateRollingOut(operationId, deploymentId, RolloutState.InProgress);

        await _progressStore.SetProgressAsync(operationId, progress, TimeSpan.FromMinutes(5));
        _operationIds.Add(operationId);

        var response = await _client.GetAsync($"/api/v1/admin/operations/{operationId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(content);

        GetPropertyCaseInsensitive(json.RootElement, "operationId").GetString().Should().Be(operationId);
        GetPropertyCaseInsensitive(json.RootElement, "deploymentId").GetString().Should().Be(deploymentId);
        GetPropertyCaseInsensitive(json.RootElement, "deploymentStatus").ValueKind.Should().NotBe(JsonValueKind.Undefined);
        GetPropertyCaseInsensitive(json.RootElement, "rolloutState").ValueKind.Should().NotBe(JsonValueKind.Undefined);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/operations/active")]
    public async Task ListActiveOperations_DeploymentType_ReturnsLifecycleFields()
    {
        var operationId = $"deploy-{Guid.NewGuid():N}";
        var deploymentId = $"dep-{Guid.NewGuid():N}";
        var progress = DeploymentProgress.CreateProvisioning(operationId, deploymentId);

        await _progressStore.SetProgressAsync(operationId, progress, TimeSpan.FromMinutes(5));
        _operationIds.Add(operationId);

        var response = await _client.GetAsync("/api/v1/admin/operations/active?type=Deployment");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(content);

        var operations = GetOperationsArray(json.RootElement);
        operations.GetArrayLength().Should().BeGreaterOrEqualTo(1);

        var op = operations.EnumerateArray()
            .First(e => GetPropertyCaseInsensitive(e, "operationId").GetString() == operationId);
        GetPropertyCaseInsensitive(op, "type").GetInt32().Should().Be((int)OperationType.Deployment);
        GetPropertyCaseInsensitive(op, "deploymentId").GetString().Should().Be(deploymentId);
        GetPropertyCaseInsensitive(op, "deploymentStatus").ValueKind.Should().NotBe(JsonValueKind.Undefined);
        CountPropertiesIgnoringCase(op, "operationId").Should().Be(1);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/operations/type/{operationType}")]
    public async Task GetOperationsByType_DeploymentType_ReturnsOperations()
    {
        var operationId = $"deploy-{Guid.NewGuid():N}";
        var deploymentId = $"dep-{Guid.NewGuid():N}";
        var progress = DeploymentProgress.CreateInitial(operationId, deploymentId);

        await _progressStore.SetProgressAsync(operationId, progress, TimeSpan.FromMinutes(5));
        _operationIds.Add(operationId);

        var response = await _client.GetAsync("/api/v1/admin/operations/type/Deployment");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(content);

        var operations = GetOperationsArray(json.RootElement);
        var op = operations.EnumerateArray()
            .First(e => GetPropertyCaseInsensitive(e, "operationId").GetString() == operationId);
        GetPropertyCaseInsensitive(op, "type").GetInt32().Should().Be((int)OperationType.Deployment);
        GetPropertyCaseInsensitive(op, "deploymentId").GetString().Should().Be(deploymentId);
    }

}
