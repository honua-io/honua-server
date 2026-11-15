// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Infrastructure.MultiTenancy;
using Honua.Infrastructure.Middleware;
using Microsoft.AspNetCore.Http;
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

public sealed partial class TileExportJobServiceTests
{
    [UnitTest]
    [Operation(Operations.Export)]
    public async Task Submit_LegacyTenantlessKey_SingleTenantUpgradeReplaysOriginal()
    {
        var tenant = new RequestTenantContext();
        var store = new InMemoryExecutionJobStore();
        var service = new TileExportJobService(TimeProvider.System, StorageOptions(),
            NullLogger<TileExportJobService>.Instance, store, new InMemoryJobQueue(), tenantContext: tenant);
        var principal = Principal(Owner);
        principal.Identities.First().AddClaim(new Claim("iss", "https://original.example"));
        var original = await service.SubmitAsync(CreatePlan(), "upgrade-key", null, principal, default);
        tenant.SetSingleTenantDefault("public");

        var replay = await service.SubmitAsync(CreatePlan(), "upgrade-key", null, principal, default);
        replay.OperationId.Should().Be(original.OperationId);
        tenant.Set("public", TenantContextSource.Claim);
        var exact = await service.SubmitAsync(CreatePlan(), "upgrade-key", null, principal, default);
        exact.OperationId.Should().NotBe(original.OperationId);
    }

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

}
