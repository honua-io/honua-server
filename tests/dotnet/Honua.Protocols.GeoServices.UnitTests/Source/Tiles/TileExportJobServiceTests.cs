// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Infrastructure.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Infrastructure.Tiles;
using Honua.Infrastructure.Security;
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
        job.Audit.RequestedBy.Should().Be(CanonicalSecurityActor.Resolve(Principal(Owner))!.ActorId);
        job.Audit.CorrelationId.Should().Be("corr-1");
        job.Audit.RequestFingerprint.Should().NotBeNullOrEmpty();
        job.Concurrency.PartitionKey.Should().StartWith("tile-export:map:");
        (await queue.GetQueueDepthAsync()).Should().Be(1);
        (await store.GetAsync(job.OperationId)).Should().NotBeNull();
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task Submit_RuntimeRegistration_CapturesEffectiveTenantAndConfiguredRoles()
    {
        var tenant = Substitute.For<ITenantContext>();
        tenant.TenantId.Returns("tenant-effective");
        await using var provider = new ServiceCollection().AddLogging()
            .AddSingleton(StorageOptions())
            .AddSingleton<IOptions<RbacOptions>>(Options.Create(new RbacOptions { RoleClaimType = "custom-role" }))
            .AddSingleton(tenant)
            .AddSingleton<Honua.Core.Features.ControlPlane.Abstractions.IExecutionJobStore>(new InMemoryExecutionJobStore())
            .AddSingleton<IJobQueue>(new InMemoryJobQueue())
            .AddTileExportRuntime().BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        await using var scope = provider.CreateAsyncScope();
        var principal = Principal(Owner);
        ((ClaimsIdentity)principal.Identity!).AddClaims(
            [new Claim("tenant_id", "tenant-token"), new Claim("custom-role", "reader")]);

        var job = await scope.ServiceProvider.GetRequiredService<ITileExportJobService>()
            .SubmitAsync(CreatePlan(), null, null, principal, default);

        job.Audit.SubmitterSecurityContext.Should().NotBeNull();
        job.Audit.SubmitterSecurityContext!.TenantId.Should().Be("tenant-effective");
        job.Audit.SubmitterSecurityContext.RoleClaimType.Should().Be("custom-role");
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task Submit_CapturesSubmitterClaimsAndTenant()
    {
        var service = CreateService(new InMemoryExecutionJobStore(), new InMemoryJobQueue());
        var principal = Principal(Owner, "reader");
        ((ClaimsIdentity)principal.Identity!).AddClaims(
            [new Claim("tenant_id", "tenant-1"), new Claim("department", "planning")]);

        var job = await service.SubmitAsync(CreatePlan(), null, null, principal, default);

        job.Audit.SubmitterSecurityContext.Should().NotBeNull();
        job.Audit.SubmitterSecurityContext!.TenantId.Should().Be("tenant-1");
        job.Audit.SubmitterSecurityContext.Claims.Should().Contain(
            claim => claim.Type == "department" && claim.Value == "planning");
        job.Audit.SubmitterSecurityContext.Claims.Should().Contain(
            claim => claim.Type == ClaimTypes.Role && claim.Value == "reader");
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task Submit_KeyedRetryWithUpdatedToken_PreservesOriginalSnapshot()
    {
        var service = CreateService(new InMemoryExecutionJobStore(), new InMemoryJobQueue());
        var principal = Principal(Owner, "reader");
        ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim("exp", "100"));
        var first = await service.SubmitAsync(CreatePlan(), "retry-key", null, principal, default);
        var refreshed = Principal(Owner, "reader");
        ((ClaimsIdentity)refreshed.Identity!).AddClaim(new Claim("exp", "200"));

        var replay = await service.SubmitAsync(CreatePlan(), "retry-key", null, refreshed, default);

        replay.OperationId.Should().Be(first.OperationId);
        replay.Audit.SubmitterSecurityContext!.Claims.Should().Contain(
            claim => claim.Type == "exp" && claim.Value == "100");
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task Submit_PublicationScopedRaster_UsesStorageLayerAdmissionPartition()
    {
        var store = new InMemoryExecutionJobStore();
        var service = CreateService(store, new InMemoryJobQueue());
        var plan = CreateRasterPlan("publication:binding:layer:7");

        var job = await service.SubmitAsync(plan, idempotencyKey: null, correlationId: null, Principal(Owner), default);

        job.Concurrency.PartitionKey.Should().Be("tile-export:raster:7");
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
    public async Task Submit_SameKeyDifferentPrincipal_CreatesSeparateJobs()
    {
        var service = CreateService(new InMemoryExecutionJobStore(), new InMemoryJobQueue());
        var first = await service.SubmitAsync(CreatePlan(), "key-1", null, Principal(Owner), default);
        var second = await service.SubmitAsync(CreatePlan(), "key-1", null, Principal(Other), default);

        second.OperationId.Should().NotBe(first.OperationId);
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
    public async Task GetStatus_RecordWithoutSubmitterContext_ReturnsNotFound()
    {
        var store = new InMemoryExecutionJobStore();
        var service = CreateService(store, new InMemoryJobQueue());
        var job = await service.SubmitAsync(CreatePlan(), null, null, Principal(Owner), default);
        await store.SetAsync(job with { Audit = job.Audit with { SubmitterSecurityContext = null } });

        await FluentActions.Awaiting(() => service.GetStatusAsync(job.OperationId, ScopeFor(CreatePlan()), Principal(Owner), default))
            .Should().ThrowAsync<TileExportNotFoundException>();
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

    [Theory]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    [InlineData("status")]
    [InlineData("cancel")]
    [InlineData("result")]
    public async Task JobAccess_DifferentApiKeyWithSameDisplayName_ReturnsNotFound(string operation)
    {
        var service = CreateService(new InMemoryExecutionJobStore(), new InMemoryJobQueue());
        var owner = ApiKeyPrincipal("11111111-1111-1111-1111-111111111111");
        var other = ApiKeyPrincipal("22222222-2222-2222-2222-222222222222");
        var job = await service.SubmitAsync(CreatePlan(), null, null, owner, default);

        Func<Task> act = operation switch
        {
            "cancel" => () => service.CancelAsync(job.OperationId, ScopeFor(CreatePlan()), other, default),
            "result" => () => service.GetResultAsync(job.OperationId, ScopeFor(CreatePlan()), other, default),
            _ => () => service.GetStatusAsync(job.OperationId, ScopeFor(CreatePlan()), other, default)
        };
        await act.Should().ThrowAsync<TileExportNotFoundException>();
        (await service.GetStatusAsync(job.OperationId, ScopeFor(CreatePlan()), owner, default))
            .OperationId.Should().Be(job.OperationId);
    }

    [Theory]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    [InlineData(false, "tenant-a", "tenant-b")]
    [InlineData(true, "tenant-a", "tenant-b")]
    [InlineData(true, "tenant-a", null)]
    [InlineData(false, null, "tenant-b")]
    public async Task JobAccess_DifferentTenant_ReturnsNotFound(bool admin, string? submittedTenant, string? requestTenant)
    {
        var store = new InMemoryExecutionJobStore();
        var service = CreateService(store, new InMemoryJobQueue());
        var owner = TenantPrincipal(Owner, submittedTenant);
        var job = await service.SubmitAsync(CreatePlan(), null, null, owner, default);
        await store.SetAsync(job with
        {
            Audit = job.Audit with
            {
                SubmitterSecurityContext = new Honua.Core.Features.Authorization.Domain.JobSecurityContext(
                Owner, submittedTenant, [], ClaimTypes.Role)
            }
        });
        var caller = TenantPrincipal(Owner, requestTenant);
        if (admin) ((ClaimsIdentity)caller.Identity!).AddClaim(new Claim(ClaimTypes.Role, "admin"));

        Func<Task>[] operations =
        [
            () => service.GetStatusAsync(job.OperationId, ScopeFor(CreatePlan()), caller, default),
            () => service.CancelAsync(job.OperationId, ScopeFor(CreatePlan()), caller, default),
            () => service.GetResultAsync(job.OperationId, ScopeFor(CreatePlan()), caller, default)
        ];
        foreach (var operation in operations)
            await operation.Should().ThrowAsync<TileExportNotFoundException>();
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task JobAccess_SameSubjectDifferentIssuer_ReturnsNotFound()
    {
        var service = CreateService(new InMemoryExecutionJobStore(), new InMemoryJobQueue());
        var owner = Principal(Owner);
        ((ClaimsIdentity)owner.Identity!).AddClaim(new Claim("iss", "issuer-a"));
        var job = await service.SubmitAsync(CreatePlan(), null, null, owner, default);
        var caller = Principal(Owner);
        ((ClaimsIdentity)caller.Identity!).AddClaim(new Claim("iss", "issuer-b"));

        await FluentActions.Awaiting(() => service.GetStatusAsync(job.OperationId, ScopeFor(CreatePlan()), caller, default))
            .Should().ThrowAsync<TileExportNotFoundException>();
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task Submit_NameOnlyIdentity_RefusesSubmission()
    {
        var service = CreateService(new InMemoryExecutionJobStore(), new InMemoryJobQueue());
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "display-name")], "test"));

        await FluentActions.Awaiting(() => service.SubmitAsync(CreatePlan(), null, null, principal, default))
            .Should().ThrowAsync<TileExportValidationException>();
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task Submit_SameKeyDifferentTenant_CreatesSeparateJobs()
    {
        var service = CreateService(new InMemoryExecutionJobStore(), new InMemoryJobQueue());
        var first = await service.SubmitAsync(CreatePlan(), "tenant-key", null, TenantPrincipal(Owner, "tenant-a"), default);
        var second = await service.SubmitAsync(CreatePlan(), "tenant-key", null, TenantPrincipal(Owner, "tenant-b"), default);

        second.OperationId.Should().NotBe(first.OperationId);
    }

    [UnitTheory]
    [InlineData("tenant-effective")]
    [InlineData(null)]
    [Operation(Operations.Export)]
    public async Task JobAccess_EffectiveTenantContext_ControlsSubmissionAndEveryLifecycleOperation(string? tenantId)
    {
        var tenant = Substitute.For<ITenantContext>();
        tenant.TenantId.Returns(tenantId);
        var service = new TileExportJobService(TimeProvider.System, StorageOptions(),
            NullLogger<TileExportJobService>.Instance, new InMemoryExecutionJobStore(),
            new InMemoryJobQueue(), tenantContext: tenant);
        var principal = TenantPrincipal(Owner, "tenant-token");
        var job = await service.SubmitAsync(CreatePlan(), "effective-key", null, principal, default);
        job.Audit.SubmitterSecurityContext!.TenantId.Should().Be(tenantId);
        (await service.GetStatusAsync(job.OperationId, ScopeFor(CreatePlan()), principal, default)).OperationId.Should().Be(job.OperationId);
        tenant.TenantId.Returns("tenant-other");
        var otherJob = await service.SubmitAsync(CreatePlan(), "effective-key", null, principal, default);
        otherJob.OperationId.Should().NotBe(job.OperationId);
        Func<Task>[] operations =
        [
            () => service.GetStatusAsync(job.OperationId, ScopeFor(CreatePlan()), principal, default),
            () => service.CancelAsync(job.OperationId, ScopeFor(CreatePlan()), principal, default),
            () => service.GetResultAsync(job.OperationId, ScopeFor(CreatePlan()), principal, default)
        ];
        foreach (var operation in operations)
        {
            await operation.Should().ThrowAsync<TileExportNotFoundException>();
        }
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task JobAccess_UnauthenticatedAdminRole_ReturnsNotFound()
    {
        var service = CreateService(new InMemoryExecutionJobStore(), new InMemoryJobQueue());
        var job = await service.SubmitAsync(CreatePlan(), null, null, Principal(Owner), default);
        var caller = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "admin")]));

        await FluentActions.Awaiting(() => service.GetStatusAsync(job.OperationId, ScopeFor(CreatePlan()), caller, default))
            .Should().ThrowAsync<TileExportNotFoundException>();
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task Submit_SameKeyDifferentApiKey_CreatesSeparateJobs()
    {
        var service = CreateService(new InMemoryExecutionJobStore(), new InMemoryJobQueue());
        var first = await service.SubmitAsync(CreatePlan(), "api-key-retry", null,
            ApiKeyPrincipal("11111111-1111-1111-1111-111111111111"), default);
        var second = await service.SubmitAsync(CreatePlan(), "api-key-retry", null,
            ApiKeyPrincipal("22222222-2222-2222-2222-222222222222"), default);

        second.OperationId.Should().NotBe(first.OperationId);
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task JobAccess_RecordWithUnqualifiedOwnerMetadata_ReturnsNotFound()
    {
        var store = new InMemoryExecutionJobStore();
        var service = CreateService(store, new InMemoryJobQueue());
        var principal = Principal(Owner);
        var job = await service.SubmitAsync(CreatePlan(), null, null, principal, default);
        var snapshot = job.Audit.SubmitterSecurityContext!;
        await store.SetAsync(job with
        {
            Audit = job.Audit with
            {
                SubmitterSecurityContext = new Honua.Core.Features.Authorization.Domain.JobSecurityContext(
                job.Audit.RequestedBy, snapshot.TenantId, snapshot.Claims, snapshot.RoleClaimType)
            }
        });

        await FluentActions.Awaiting(() => service.GetStatusAsync(job.OperationId, ScopeFor(CreatePlan()), principal, default))
            .Should().ThrowAsync<TileExportNotFoundException>();
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task JobAccess_SerializedRecord_PreservesDurableOwnerMetadata()
    {
        var store = new InMemoryExecutionJobStore();
        var service = CreateService(store, new InMemoryJobQueue());
        var principal = Principal(Owner);
        var job = await service.SubmitAsync(CreatePlan(), null, null, principal, default);
        var json = System.Text.Json.JsonSerializer.Serialize(job, Honua.ControlPlane.ControlPlaneJsonContext.Default.ExecutionJobRecord);
        var restored = System.Text.Json.JsonSerializer.Deserialize(json, Honua.ControlPlane.ControlPlaneJsonContext.Default.ExecutionJobRecord)!;
        restored.Audit.SubmitterSecurityContext!.OwnerActorId.Should().Be(job.Audit.RequestedBy);
        restored.Audit.SubmitterSecurityContext.WorkspaceOwnerId.Should().Be(Owner);
        await store.SetAsync(restored);

        (await service.GetStatusAsync(job.OperationId, ScopeFor(CreatePlan()), principal, default)).OperationId.Should().Be(job.OperationId);
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task Submit_KeyOfPriorFormatRecord_ReplaysForSameSubjectAndConflictsForOthers()
    {
        var store = new InMemoryExecutionJobStore();
        var service = CreateService(store, new InMemoryJobQueue());
        var submitted = await service.SubmitAsync(CreatePlan(), null, null, Principal(Owner), default);
        var priorJobId = "te-" + Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("prior-key")).AsSpan(0, 12));
        (await store.TryCreateAsync(submitted with
        {
            OperationId = priorJobId,
            Audit = submitted.Audit with
            {
                IdempotencyKey = "prior-key",
                RequestedBy = Owner,
                SubmitterSecurityContext = submitted.Audit.SubmitterSecurityContext! with
                {
                    PrincipalId = Owner,
                    OwnerActorId = null,
                    WorkspaceOwnerId = null
                }
            }
        })).Should().BeTrue();

        var replay = await service.SubmitAsync(CreatePlan(), "prior-key", null, Principal(Owner), default);
        var status = await service.GetStatusAsync(priorJobId, ScopeFor(CreatePlan()), Principal(Owner), default);

        replay.OperationId.Should().Be(priorJobId);
        status.OperationId.Should().Be(priorJobId);
        await FluentActions.Awaiting(() => service.SubmitAsync(CreatePlan(), "prior-key", null, Principal(Other), default))
            .Should().ThrowAsync<TileExportIdempotencyConflictException>();
        await FluentActions.Awaiting(() => service.GetStatusAsync(priorJobId, ScopeFor(CreatePlan()), Principal(Other), default))
            .Should().ThrowAsync<TileExportNotFoundException>();
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task JobAccess_PriorFormatApiKeyRecord_MatchesCapturedKeyIdNotDisplayName()
    {
        // The prior tile resolver never read api_key_id: an API-key submission stored the key's
        // display name in RequestedBy while the snapshot captured the key id.
        const string ownerKey = "11111111-1111-1111-1111-111111111111";
        var store = new InMemoryExecutionJobStore();
        var service = CreateService(store, new InMemoryJobQueue());
        var submitted = await service.SubmitAsync(CreatePlan(), null, null, ApiKeyPrincipal(ownerKey), default);
        var priorJobId = "te-" + Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("prior-key")).AsSpan(0, 12));
        (await store.TryCreateAsync(submitted with
        {
            OperationId = priorJobId,
            Audit = submitted.Audit with
            {
                IdempotencyKey = "prior-key",
                RequestedBy = "shared-name",
                SubmitterSecurityContext = submitted.Audit.SubmitterSecurityContext! with
                {
                    PrincipalId = "shared-name",
                    OwnerActorId = null,
                    WorkspaceOwnerId = null
                }
            }
        })).Should().BeTrue();
        var other = ApiKeyPrincipal("22222222-2222-2222-2222-222222222222");

        (await service.GetStatusAsync(priorJobId, ScopeFor(CreatePlan()), ApiKeyPrincipal(ownerKey), default))
            .OperationId.Should().Be(priorJobId);
        (await service.SubmitAsync(CreatePlan(), "prior-key", null, ApiKeyPrincipal(ownerKey), default))
            .OperationId.Should().Be(priorJobId);
        await FluentActions.Awaiting(() => service.GetStatusAsync(priorJobId, ScopeFor(CreatePlan()), other, default))
            .Should().ThrowAsync<TileExportNotFoundException>();
        await FluentActions.Awaiting(() => service.CancelAsync(priorJobId, ScopeFor(CreatePlan()), other, default))
            .Should().ThrowAsync<TileExportNotFoundException>();
        await FluentActions.Awaiting(() => service.SubmitAsync(CreatePlan(), "prior-key", null, other, default))
            .Should().ThrowAsync<TileExportIdempotencyConflictException>();
        await FluentActions.Awaiting(() => service.GetStatusAsync(priorJobId, ScopeFor(CreatePlan()), Principal("shared-name"), default))
            .Should().ThrowAsync<TileExportNotFoundException>();

        await service.CancelAsync(priorJobId, ScopeFor(CreatePlan()), ApiKeyPrincipal(ownerKey), default);
        (await store.GetAsync(priorJobId))!.Status.Should().Be(ExecutionJobStatus.Cancelled);
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task JobAccess_PriorFormatApiKeyRecord_NameDifferingFromCapturedName_ReturnsNotFound()
    {
        const string ownerKey = "11111111-1111-1111-1111-111111111111";
        var store = new InMemoryExecutionJobStore();
        var service = CreateService(store, new InMemoryJobQueue());
        var job = await service.SubmitAsync(CreatePlan(), null, null, ApiKeyPrincipal(ownerKey), default);
        await store.SetAsync(job with
        {
            Audit = job.Audit with
            {
                RequestedBy = "another-name",
                SubmitterSecurityContext = job.Audit.SubmitterSecurityContext! with { OwnerActorId = null, WorkspaceOwnerId = null }
            }
        });

        await FluentActions.Awaiting(() => service.GetStatusAsync(job.OperationId, ScopeFor(CreatePlan()), ApiKeyPrincipal(ownerKey), default))
            .Should().ThrowAsync<TileExportNotFoundException>();
    }

    private static ClaimsPrincipal ApiKeyPrincipal(string id)
        => new(new ClaimsIdentity([new Claim("api_key_id", id), new Claim(ClaimTypes.Name, "shared-name")], AuthenticationExtensions.ApiKeyScheme));

    private static ClaimsPrincipal TenantPrincipal(string id, string? tenant)
    {
        var principal = Principal(id);
        if (tenant is not null) ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim("tenant_id", tenant));
        return principal;
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

    private static TileExportJobPlan CreateRasterPlan(string resourceId)
        => new()
        {
            SourceKind = TileExportSourceKind.Raster,
            ResourceId = resourceId,
            Source = new TileExportRasterSourceDescriptor(
                42,
                "7",
                "First",
                null,
                "same-membership"),
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
