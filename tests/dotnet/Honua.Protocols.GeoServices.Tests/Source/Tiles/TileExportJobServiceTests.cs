// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Infrastructure.Tiles;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.Tiles;

/// <summary>
/// Focused tests for the protocol-neutral tile-export lifecycle service: submission,
/// idempotency, admission, ownership/binding isolation, cancellation, and result delivery.
/// </summary>
[Protocol(TestProtocols.MapServer)]
public sealed class TileExportJobServiceTests
{
    private const string Owner = "user-alice";
    private const string Other = "user-bob";

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task Submit_CreatesQueuedJobAndEnqueues()
    {
        var store = new InMemoryExecutionJobStore();
        var queue = new InMemoryJobQueue();
        var service = CreateService(store, queue);

        var job = await service.SubmitAsync(CreatePlan(), idempotencyKey: null, correlationId: "corr-1", Principal(Owner), default);

        job.Status.Should().Be(ExecutionJobStatus.Queued);
        job.Spec.Kind.Should().Be(ExecutionJobKind.TileExport);
        job.Audit.RequestedBy.Should().Be(Owner);
        job.Audit.CorrelationId.Should().Be("corr-1");
        job.Audit.RequestFingerprint.Should().NotBeNullOrEmpty();
        job.Concurrency.PartitionKey.Should().StartWith("tile-export:map:");
        (await queue.GetQueueDepthAsync()).Should().Be(1);
        (await store.GetAsync(job.OperationId)).Should().NotBeNull();
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task Submit_SamePrincipalSameKeySamePlan_ReturnsExistingJob()
    {
        var store = new InMemoryExecutionJobStore();
        var service = CreateService(store, new InMemoryJobQueue());

        var first = await service.SubmitAsync(CreatePlan(), "key-1", null, Principal(Owner), default);
        var second = await service.SubmitAsync(CreatePlan(), "key-1", null, Principal(Owner), default);

        second.OperationId.Should().Be(first.OperationId);
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task Submit_SameKeyDifferentPlan_ThrowsIdempotencyConflict()
    {
        var store = new InMemoryExecutionJobStore();
        var service = CreateService(store, new InMemoryJobQueue());

        await service.SubmitAsync(CreatePlan(), "key-1", null, Principal(Owner), default);
        var mutated = CreatePlan() with { ZoomLevels = [0, 1, 2] };

        await FluentActions.Awaiting(() => service.SubmitAsync(mutated, "key-1", null, Principal(Owner), default))
            .Should().ThrowAsync<TileExportIdempotencyConflictException>();
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task Submit_SameKeyDifferentPrincipal_ThrowsIdempotencyConflictWithoutExposingJob()
    {
        var store = new InMemoryExecutionJobStore();
        var service = CreateService(store, new InMemoryJobQueue());

        await service.SubmitAsync(CreatePlan(), "key-1", null, Principal(Owner), default);

        var act = await FluentActions.Awaiting(() => service.SubmitAsync(CreatePlan(), "key-1", null, Principal(Other), default))
            .Should().ThrowAsync<TileExportIdempotencyConflictException>();
        // The winning job id is withheld from a cross-principal replay.
        act.Which.ConflictingJobId.Should().BeNull();
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task Submit_AdmissionThrottled_ThrowsWithRetryAfter()
    {
        var admission = Substitute.For<IExecutionAdmissionEvaluator>();
        admission.EvaluateAsync(Arg.Any<ExecutionAdmissionRequest>(), Arg.Any<CancellationToken>())
            .Returns(ExecutionAdmissionDecision.Throttled(
                ExecutionAdmissionDimension.Rate, "rate:tileexport:per-principal", "slow down", 42, new ExecutionAdmissionSnapshot()));
        var service = CreateService(new InMemoryExecutionJobStore(), new InMemoryJobQueue(), admission: admission);

        var act = await FluentActions.Awaiting(() => service.SubmitAsync(CreatePlan(), null, null, Principal(Owner), default))
            .Should().ThrowAsync<TileExportAdmissionException>();
        act.Which.Outcome.Should().Be(ExecutionAdmissionOutcome.Throttled);
        act.Which.RetryAfterSeconds.Should().Be(42);
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task Submit_InvalidPlan_ThrowsValidation()
    {
        var service = CreateService(new InMemoryExecutionJobStore(), new InMemoryJobQueue());
        var invalid = CreatePlan() with { East = -200 };

        await FluentActions.Awaiting(() => service.SubmitAsync(invalid, null, null, Principal(Owner), default))
            .Should().ThrowAsync<TileExportValidationException>();
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task Submit_WithoutStore_ThrowsStoreUnavailable()
    {
        var service = new TileExportJobService(
            TimeProvider.System, StorageOptions(), NullLogger<TileExportJobService>.Instance, jobStore: null);

        await FluentActions.Awaiting(() => service.SubmitAsync(CreatePlan(), null, null, Principal(Owner), default))
            .Should().ThrowAsync<TileExportStoreUnavailableException>();
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task GetStatus_Owner_ReturnsJob()
    {
        var store = new InMemoryExecutionJobStore();
        var service = CreateService(store, new InMemoryJobQueue());
        var job = await service.SubmitAsync(CreatePlan(), null, null, Principal(Owner), default);

        var fetched = await service.GetStatusAsync(job.OperationId, ScopeFor(CreatePlan()), Principal(Owner), default);

        fetched.OperationId.Should().Be(job.OperationId);
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task GetStatus_DifferentPrincipal_ReturnsNotFound()
    {
        var store = new InMemoryExecutionJobStore();
        var service = CreateService(store, new InMemoryJobQueue());
        var job = await service.SubmitAsync(CreatePlan(), null, null, Principal(Owner), default);

        await FluentActions.Awaiting(() => service.GetStatusAsync(job.OperationId, ScopeFor(CreatePlan()), Principal(Other), default))
            .Should().ThrowAsync<TileExportNotFoundException>();
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task GetStatus_MismatchedResourceBinding_ReturnsNotFound()
    {
        var store = new InMemoryExecutionJobStore();
        var service = CreateService(store, new InMemoryJobQueue());
        var job = await service.SubmitAsync(CreatePlan(), null, null, Principal(Owner), default);

        var otherScope = new TileExportJobScope(TileExportSourceKind.Map, "different-service");
        await FluentActions.Awaiting(() => service.GetStatusAsync(job.OperationId, otherScope, Principal(Owner), default))
            .Should().ThrowAsync<TileExportNotFoundException>();
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task GetStatus_Admin_ReturnsAnyOwnersJob()
    {
        var store = new InMemoryExecutionJobStore();
        var service = CreateService(store, new InMemoryJobQueue());
        var job = await service.SubmitAsync(CreatePlan(), null, null, Principal(Owner), default);

        var fetched = await service.GetStatusAsync(job.OperationId, ScopeFor(CreatePlan()), Principal(Other, "admin"), default);

        fetched.OperationId.Should().Be(job.OperationId);
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task GetResult_BeforeTerminal_ThrowsPrecondition()
    {
        var store = new InMemoryExecutionJobStore();
        var service = CreateService(store, new InMemoryJobQueue());
        var job = await service.SubmitAsync(CreatePlan(), null, null, Principal(Owner), default);

        await FluentActions.Awaiting(() => service.GetResultAsync(job.OperationId, ScopeFor(CreatePlan()), Principal(Owner), default))
            .Should().ThrowAsync<TileExportPreconditionFailedException>();
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task GetResult_Succeeded_MintsFreshPresignedUrl()
    {
        var store = new InMemoryExecutionJobStore();
        var storage = Substitute.For<ICloudFileStorage>();
        var expiresAt = DateTimeOffset.UtcNow.AddHours(1);
        storage.GetMetadataAsync("artifact-key", Arg.Any<CancellationToken>())
            .Returns(StoredArtifact("artifact-key", expiresAt, 4096));
        storage.GetPresignedUrlAsync("artifact-key", Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns("https://signed.example/artifact-key");
        var service = CreateService(store, new InMemoryJobQueue(), storage: storage);

        var job = await service.SubmitAsync(CreatePlan(), null, null, Principal(Owner), default);
        await MarkSucceededAsync(store, job.OperationId, "artifact-key");

        var result = await service.GetResultAsync(job.OperationId, ScopeFor(CreatePlan()), Principal(Owner), default);

        result.DownloadUrl.Should().Be("https://signed.example/artifact-key");
        result.ExpiresAt.Should().Be(expiresAt);
        result.SizeBytes.Should().Be(4096);
        result.Format.Should().Be(TileExportPackageFormat.Tpkx);
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task GetResult_ExpiredArtifact_ReturnsNotFound()
    {
        var store = new InMemoryExecutionJobStore();
        var storage = Substitute.For<ICloudFileStorage>();
        storage.GetMetadataAsync("artifact-key", Arg.Any<CancellationToken>())
            .Returns(StoredArtifact("artifact-key", DateTimeOffset.UtcNow.AddMinutes(-1), 4096));
        var service = CreateService(store, new InMemoryJobQueue(), storage: storage);

        var job = await service.SubmitAsync(CreatePlan(), null, null, Principal(Owner), default);
        await MarkSucceededAsync(store, job.OperationId, "artifact-key");

        await FluentActions.Awaiting(() => service.GetResultAsync(job.OperationId, ScopeFor(CreatePlan()), Principal(Owner), default))
            .Should().ThrowAsync<TileExportNotFoundException>();
        await storage.DidNotReceive().GetPresignedUrlAsync(Arg.Any<string>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task Cancel_QueuedJob_TransitionsToCancelledAndDequeues()
    {
        var store = new InMemoryExecutionJobStore();
        var queue = new InMemoryJobQueue();
        var service = CreateService(store, queue);
        var job = await service.SubmitAsync(CreatePlan(), null, null, Principal(Owner), default);

        await service.CancelAsync(job.OperationId, ScopeFor(CreatePlan()), Principal(Owner), default);

        (await store.GetAsync(job.OperationId))!.Status.Should().Be(ExecutionJobStatus.Cancelled);
        (await queue.GetQueueDepthAsync()).Should().Be(0);
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task Cancel_ClaimedJob_StampsCancellationRequest()
    {
        var store = new InMemoryExecutionJobStore();
        var service = CreateService(store, new InMemoryJobQueue());
        var job = await service.SubmitAsync(CreatePlan(), null, null, Principal(Owner), default);
        await store.SetAsync((await store.GetAsync(job.OperationId))! with
        {
            Status = ExecutionJobStatus.Running,
            ClaimedBy = "worker-1"
        });

        await service.CancelAsync(job.OperationId, ScopeFor(CreatePlan()), Principal(Owner), default);

        var updated = (await store.GetAsync(job.OperationId))!;
        updated.Status.Should().Be(ExecutionJobStatus.Running);
        updated.CancellationRequestedAt.Should().NotBeNull();
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task Cancel_TerminalJob_ThrowsPrecondition()
    {
        var store = new InMemoryExecutionJobStore();
        var service = CreateService(store, new InMemoryJobQueue());
        var job = await service.SubmitAsync(CreatePlan(), null, null, Principal(Owner), default);
        await MarkSucceededAsync(store, job.OperationId, "artifact-key");

        await FluentActions.Awaiting(() => service.CancelAsync(job.OperationId, ScopeFor(CreatePlan()), Principal(Owner), default))
            .Should().ThrowAsync<TileExportPreconditionFailedException>();
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task Cancel_AlreadyCancelled_IsIdempotent()
    {
        var store = new InMemoryExecutionJobStore();
        var service = CreateService(store, new InMemoryJobQueue());
        var job = await service.SubmitAsync(CreatePlan(), null, null, Principal(Owner), default);
        await service.CancelAsync(job.OperationId, ScopeFor(CreatePlan()), Principal(Owner), default);

        await service.CancelAsync(job.OperationId, ScopeFor(CreatePlan()), Principal(Owner), default);

        (await store.GetAsync(job.OperationId))!.Status.Should().Be(ExecutionJobStatus.Cancelled);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static TileExportJobService CreateService(
        InMemoryExecutionJobStore store,
        InMemoryJobQueue queue,
        ICloudFileStorage? storage = null,
        IExecutionAdmissionEvaluator? admission = null)
        => new(
            TimeProvider.System,
            StorageOptions(),
            NullLogger<TileExportJobService>.Instance,
            store,
            queue,
            storage,
            admission);

    private static IOptions<CloudStorageOptions> StorageOptions()
        => Options.Create(new CloudStorageOptions());

    private static async Task MarkSucceededAsync(InMemoryExecutionJobStore store, string jobId, string artifactReference)
    {
        var job = (await store.GetAsync(jobId))!;
        await store.SetAsync(job with
        {
            Status = ExecutionJobStatus.Succeeded,
            CompletedAt = DateTimeOffset.UtcNow,
            ArtifactReferences = [artifactReference]
        });
    }

    private static TileExportJobScope ScopeFor(TileExportJobPlan plan)
        => new(plan.SourceKind, plan.ResourceId);

    private static ClaimsPrincipal Principal(string? id, params string[] roles)
    {
        var claims = new List<Claim>();
        if (id is not null)
        {
            claims.Add(new Claim(ClaimTypes.NameIdentifier, id));
        }

        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "test"));
    }

    private static CloudFile StoredArtifact(string key, DateTimeOffset expiresAt, long sizeBytes)
        => new()
        {
            FileId = key,
            FileName = Path.GetFileName(key),
            StoragePath = key,
            ContentType = "application/octet-stream",
            SizeBytes = sizeBytes,
            UploadedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            ExpiresAt = expiresAt,
            Provider = CloudStorageProvider.Local
        };

    private static TileExportJobPlan CreatePlan()
        => new()
        {
            SourceKind = TileExportSourceKind.Map,
            ResourceId = "world-basemap",
            Source = new TileExportMapSourceDescriptor(
                42,
                [new("0", "default", 1)],
                "provider-revision-9",
                null),
            ZoomLevels = [0, 2],
            West = -180,
            South = -85,
            East = 180,
            North = 85,
            TileImageFormat = "PNG",
            PackageFormat = TileExportPackageFormat.Tpkx,
            MaxTiles = 10_000,
            MaxArtifactBytes = 1024 * 1024,
            RetentionSeconds = 3600
        };
}
