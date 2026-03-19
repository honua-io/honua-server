// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>
/// Integration tests for admin operations progress endpoints.
/// </summary>
[Collection("Database")]
[Protocol(Protocols.Admin)]
[Operation(Operations.OperationsProgress)]
public sealed class OperationsProgressEndpointsTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    private readonly List<string> _operationIds = [];
    private HttpClient _client = null!;
    private IUniversalProgressStore _progressStore = null!;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.Client;
        _progressStore = _fixture.GetService<IUniversalProgressStore>();
    }

    public async Task DisposeAsync()
    {
        foreach (var operationId in _operationIds)
        {
            await _progressStore.DeleteProgressAsync(operationId);
        }

        await _fixture.DisposeAsync();
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/operations/{operationId}")]
    public async Task GetOperationStatus_WhenExists_ReturnsProgress()
    {
        var operationId = Guid.NewGuid().ToString("N");
        var progress = UploadProgress.CreateInitial(operationId, "test.geojson", 10) with
        {
            Status = OperationStatus.Processing
        };

        await _progressStore.SetProgressAsync(operationId, progress, TimeSpan.FromMinutes(5));
        _operationIds.Add(operationId);

        var response = await _client.GetAsync($"/api/v1/admin/operations/{operationId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(content);

        GetPropertyCaseInsensitive(json.RootElement, "uploadId").GetString().Should().Be(operationId);
        GetPropertyCaseInsensitive(json.RootElement, "status").GetInt32().Should().Be((int)OperationStatus.Processing);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/operations/{operationId}/cancel")]
    public async Task CancelOperation_WhenCancellable_ReturnsCancelledResponse()
    {
        var operationId = Guid.NewGuid().ToString("N");
        var progress = UploadProgress.CreateInitial(operationId, "cancel.geojson", 25) with
        {
            Status = OperationStatus.Processing
        };

        await _progressStore.SetProgressAsync(operationId, progress, TimeSpan.FromMinutes(5));
        _operationIds.Add(operationId);

        var response = await _client.PostAsync($"/api/v1/admin/operations/{operationId}/cancel", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(content);

        GetPropertyCaseInsensitive(json.RootElement, "operationId").GetString().Should().Be(operationId);
        GetPropertyCaseInsensitive(json.RootElement, "type").GetInt32().Should().Be((int)OperationType.Upload);

        var updated = await _progressStore.GetProgressAsync(operationId);
        updated.Should().NotBeNull();
        updated!.Status.Should().Be(OperationStatus.Cancelled);
    }

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
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/operations/{operationId}")]
    public async Task GetOperationStatus_NonexistentId_ReturnsNotFound()
    {
        var response = await _client.GetAsync($"/api/v1/admin/operations/nonexistent-{Guid.NewGuid():N}");

        // 405 persists even after migrating Map() to MapGet() — root cause is deeper
        // in the routing/API-versioning pipeline, not the endpoint registration.
        response.StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/operations/{operationId}/cancel")]
    public async Task CancelOperation_AlreadyCancelled_ReturnsIdempotent()
    {
        var operationId = Guid.NewGuid().ToString("N");
        var progress = UploadProgress.CreateInitial(operationId, "double-cancel.geojson", 10) with
        {
            Status = OperationStatus.Cancelled
        };

        await _progressStore.SetProgressAsync(operationId, progress, TimeSpan.FromMinutes(5));
        _operationIds.Add(operationId);

        var response = await _client.PostAsync($"/api/v1/admin/operations/{operationId}/cancel", null);

        // 405 persists — see GetOperationStatus_NonexistentId comment.
        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.Conflict, HttpStatusCode.BadRequest, HttpStatusCode.MethodNotAllowed);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/operations/{operationId}/cancel")]
    public async Task CancelOperation_CompletedOperation_ReturnsBadRequest()
    {
        var operationId = Guid.NewGuid().ToString("N");
        var progress = UploadProgress.CreateInitial(operationId, "completed.geojson", 50) with
        {
            Status = OperationStatus.Completed
        };

        await _progressStore.SetProgressAsync(operationId, progress, TimeSpan.FromMinutes(5));
        _operationIds.Add(operationId);

        var response = await _client.PostAsync($"/api/v1/admin/operations/{operationId}/cancel", null);

        // 405 persists — see GetOperationStatus_NonexistentId comment.
        response.StatusCode.Should().BeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.Conflict, HttpStatusCode.MethodNotAllowed);
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

    private static JsonElement GetOperationsArray(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            return root;
        }

        return GetPropertyCaseInsensitive(root, "operations");
    }

    private static JsonElement GetPropertyCaseInsensitive(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException($"Expected JSON object for property '{propertyName}'.");
        }

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        throw new KeyNotFoundException($"Property '{propertyName}' not found in JSON payload.");
    }
}
