// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

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

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/metadata/releases/operations")]
    public async Task CreateMetadataReleaseOperation_WithInvalidFieldName_Returns400()
    {
        string[] invalidFieldNames =
        [
            "col'; DROP TABLE users;--",
            "1starts_with_digit",
            "bad-hyphen",
            "has space",
            "col\r\nforged",
            "owner_email\n"
        ];

        foreach (var invalidFieldName in invalidFieldNames)
        {
            var response = await _client.PostAsJsonAsync("/api/v1/admin/metadata/releases/operations", new
            {
                packageId = "pkg-invalid-field",
                targetEnvironment = "dev",
                resourceSemanticId = "res-1",
                newFieldName = invalidFieldName,
                scriptId = "add-safe-script"
            });

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        (await _workflowStore.ListActiveAsync()).Should().BeEmpty();
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/metadata/releases/operations")]
    public async Task CreateMetadataReleaseOperation_WithInvalidDataPopulateField_Returns400()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/admin/metadata/releases/operations", new
        {
            packageId = "pkg-invalid-pop",
            targetEnvironment = "dev",
            resourceSemanticId = "res-1",
            newFieldName = "owner_email",
            dataPopulateFields = new[] { "owner_email", "bad; DROP TABLE x;--" }
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var trailingNewlineResponse = await _client.PostAsJsonAsync("/api/v1/admin/metadata/releases/operations", new
        {
            packageId = "pkg-invalid-pop-lf",
            targetEnvironment = "dev",
            resourceSemanticId = "res-1",
            newFieldName = "owner_email",
            dataPopulateFields = new[] { "owner_email\n" }
        });

        trailingNewlineResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _workflowStore.ListActiveAsync()).Should().BeEmpty();
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/metadata/releases/operations")]
    public async Task CreateMetadataReleaseOperation_WithControlCharactersOrOverLengthIdentifiers_Returns400()
    {
        object[] invalidPayloads =
        [
            new { packageId = "pkg\r\nforged", targetEnvironment = "dev", resourceSemanticId = "res-1", newFieldName = "owner_email" },
            new { packageId = "pkg-1", targetEnvironment = "dev\u2028forged", resourceSemanticId = "res-1", newFieldName = "owner_email" },
            new { packageId = "pkg-1", targetEnvironment = "dev", resourceSemanticId = "res\nforged", newFieldName = "owner_email" },
            new { packageId = "pkg-1", targetEnvironment = "dev", resourceSemanticId = "res-1", newFieldName = "owner_email", scriptId = "scr\r\nforged" },
            new { packageId = "pkg-1", targetEnvironment = "dev", resourceSemanticId = "res-1", newFieldName = "owner_email", newFieldType = "String\nforged" },
            new { packageId = "pkg-1", targetEnvironment = "dev", resourceSemanticId = "res-1", newFieldName = "owner_email", reason = "ok\r\n[ERR] forged" },
            new { packageId = "pkg-1", targetEnvironment = "dev", resourceSemanticId = "res-1", newFieldName = "owner_email", idempotencyKey = "idem\nforged" },
            new { packageId = "pkg-1", targetEnvironment = "dev", resourceSemanticId = "res-1", newFieldName = "owner_email", correlationId = "corr\u0085forged" },
            new { packageId = new string('p', 129), targetEnvironment = "dev", resourceSemanticId = "res-1", newFieldName = "owner_email" },
            new { packageId = "pkg-1", targetEnvironment = "dev", resourceSemanticId = "res-1", newFieldName = "owner_email", reason = new string('r', 513) },
        ];

        foreach (var payload in invalidPayloads)
        {
            var response = await _client.PostAsJsonAsync("/api/v1/admin/metadata/releases/operations", payload);
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        (await _workflowStore.ListActiveAsync()).Should().BeEmpty();
    }

    [UnitTest]
    public async Task CoordinatedReleaseCreate_WithInvalidMetadataFieldName_RejectsBeforeRollout()
    {
        var store = new InMemoryWorkflowOperationStore();
        var coordinatedService = new Honua.ControlPlane.CoordinatedReleaseControlService([store]);
        var container = new CoordinatedReleaseContainerSpec
        {
            TargetId = "svc-1",
            DesiredImage = "honua/server:rev-2",
        };
        var invalidPlan = CreateInvalidFieldPlan("pkg-coord-invalid", "script-1");

        var act = () => coordinatedService.CreateAsync(
            container, invalidPlan, "dev", "admin", "test", "idem-1", "corr-1");

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*newFieldName*");
        (await store.ListActiveAsync()).Should().BeEmpty();
    }

    [UnitTest]
    public async Task CoordinatedMetadataStepExecutor_WhenAlreadySubmittedPlanFailsValidation_ReturnsFailedForUnwind()
    {
        var store = new InMemoryWorkflowOperationStore();
        var metadataService = new Honua.ControlPlane.MetadataReleaseControlService([store]);
        var executor = new Honua.ControlPlane.CoordinatedMetadataStepExecutor(
            metadataService,
            NSubstitute.Substitute.For<IMetadataReleaseOperationReconciler>(),
            [store]);

        var invalidPlan = CreateInvalidFieldPlan("pkg-coord-legacy", "script-legacy");
        var context = new CoordinatedReleaseContext
        {
            PackageId = invalidPlan.PackageId,
            TargetEnvironment = "dev",
            Container = new CoordinatedReleaseContainerSpec { TargetId = "svc-1", DesiredImage = "honua/server:rev-2" },
            MetadataPlan = invalidPlan,
            CurrentStep = CoordinatedReleaseStep.MetadataAndSchema,
        };
        var operation = new WorkflowOperationRecord
        {
            OperationId = "coord-op-legacy",
            Kind = WorkflowOperationKind.CoordinatedRelease,
            Status = WorkflowOperationStatus.Reconciling,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Audit = new OperationAuditInfo { RequestedBy = "admin", CorrelationId = "corr-1" },
            CoordinatedRelease = context,
        };

        var result = await executor.StartAsync(context, operation);

        result.Outcome.Should().Be(CoordinatedStepOutcome.Failed);
        result.Detail.Should().Contain("newFieldName");
    }

    private static MetadataReleaseExecutionPlan CreateInvalidFieldPlan(string packageId, string scriptId) =>
        new()
        {
            PackageId = packageId,
            TargetEnvironment = "dev",
            ResourceSemanticId = "res-1",
            NewFieldName = "owner-email",
            Script = new MetadataReleaseScript
            {
                ScriptId = scriptId,
                Reversible = true,
                ForwardOperations =
                [
                    new MetadataReleaseScriptOperation
                    {
                        Kind = MetadataReleaseScriptOperationKind.AddColumn,
                        ResourceSemanticId = "res-1",
                        FieldName = "owner-email",
                        FieldType = "String",
                        Nullable = true,
                    },
                ],
            },
        };

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
