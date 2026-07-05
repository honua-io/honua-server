// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.ControlPlane;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>
/// API-surface coverage for the additive metadata-release create endpoint. The lifecycle
/// stage-machine itself is covered by <see cref="Infrastructure.ControlPlane.MetadataReleaseReconcilerTests"/>.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Metadata)]
public sealed class MetadataReleaseControlEndpointsTests : IAsyncLifetime
{
    private readonly InMemoryWorkflowOperationStore _workflowStore = new();
    private readonly WebAppFixture _fixture;
    private HttpClient _client = null!;

    public MetadataReleaseControlEndpointsTests()
    {
        _fixture = new WebAppFixture()
            .ConfigureServices(services =>
            {
                services.RemoveAll<IWorkflowOperationStore>();
                services.AddSingleton<IWorkflowOperationStore>(_workflowStore);
                // The create service is only auto-registered behind Redis; register it explicitly here.
                services.RemoveAll<MetadataReleaseControlService>();
                services.AddSingleton<MetadataReleaseControlService>();
            });
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.CreateAdminClient();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/metadata/releases/operations")]
    public async Task CreateMetadataReleaseOperation_WithAdditiveRequest_Returns201AndSubmitsLifecycle()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/admin/metadata/releases/operations", new
        {
            packageId = $"pkg-{Guid.NewGuid():N}",
            targetEnvironment = "production",
            resourceSemanticId = "parcels",
            newFieldName = "owner_email",
            newFieldType = "String",
            dataPopulateWorkloadId = "populate-owner-email",
            reason = "Demo B additive evolution"
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        root.GetProperty("kind").GetString().Should().Be("MetadataRelease");
        root.GetProperty("status").GetString().Should().Be("Submitted");
        root.GetProperty("metadataRelease").GetProperty("currentStage").GetString().Should().Be("Preflight");

        // The operation was durably recorded so the reconciler can advance it.
        var operationId = root.GetProperty("operationId").GetString();
        var stored = await _workflowStore.GetAsync(operationId!);
        stored.Should().NotBeNull();
        stored!.Kind.Should().Be(WorkflowOperationKind.MetadataRelease);
        stored.MetadataRelease!.ExecutionPlan.Should().NotBeNull();
        stored.MetadataRelease.ExecutionPlan!.NewFieldName.Should().Be("owner_email");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/metadata/releases/operations")]
    public async Task CreateMetadataReleaseOperation_WithMissingFields_Returns400()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/admin/metadata/releases/operations", new
        {
            packageId = "pkg-1",
            targetEnvironment = "production"
            // resourceSemanticId and newFieldName intentionally omitted
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private sealed class InMemoryWorkflowOperationStore : IWorkflowOperationStore
    {
        private readonly ConcurrentDictionary<string, WorkflowOperationRecord> _operations = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, string> _metadataPackageIndex = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, string> _leases = new(StringComparer.Ordinal);

        public Task<bool> TryAcquireLeaseAsync(string operationId, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
            => Task.FromResult(_leases.TryAdd(operationId, ownerId));

        public Task<bool> RenewLeaseAsync(string operationId, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
            => Task.FromResult(_leases.TryGetValue(operationId, out var currentOwner) && currentOwner == ownerId);

        public Task ReleaseLeaseAsync(string operationId, string ownerId, CancellationToken cancellationToken = default)
        {
            _leases.TryRemove(new KeyValuePair<string, string>(operationId, ownerId));
            return Task.CompletedTask;
        }

        public Task<bool> TryCreateAsync(WorkflowOperationRecord operation, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            var created = _operations.TryAdd(operation.OperationId, operation);
            if (created)
            {
                Index(operation);
            }

            return Task.FromResult(created);
        }

        public Task<WorkflowOperationRecord?> GetAsync(string operationId, CancellationToken cancellationToken = default)
            => Task.FromResult(_operations.TryGetValue(operationId, out var operation) ? operation : null);

        public Task<WorkflowOperationRecord?> GetByMetadataPackageIdAsync(string packageId, CancellationToken cancellationToken = default)
            => Task.FromResult(
                _metadataPackageIndex.TryGetValue(packageId, out var operationId) &&
                _operations.TryGetValue(operationId, out var operation)
                    ? operation
                    : null);

        public Task SetAsync(WorkflowOperationRecord operation, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            _operations[operation.OperationId] = operation;
            Index(operation);
            return Task.CompletedTask;
        }

        public Task<bool> TrySetAsync(WorkflowOperationRecord operation, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            _operations[operation.OperationId] = operation;
            Index(operation);
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<WorkflowOperationRecord>> ListActiveAsync(WorkflowOperationKind? kind = null, CancellationToken cancellationToken = default)
        {
            var operations = _operations.Values
                .Where(operation => !kind.HasValue || operation.Kind == kind.Value)
                .ToArray();
            return Task.FromResult<IReadOnlyList<WorkflowOperationRecord>>(operations);
        }

        private void Index(WorkflowOperationRecord operation)
        {
            if (operation.Kind == WorkflowOperationKind.MetadataRelease &&
                !string.IsNullOrWhiteSpace(operation.MetadataRelease?.PackageId))
            {
                _metadataPackageIndex[operation.MetadataRelease.PackageId] = operation.OperationId;
            }
        }
    }
}
