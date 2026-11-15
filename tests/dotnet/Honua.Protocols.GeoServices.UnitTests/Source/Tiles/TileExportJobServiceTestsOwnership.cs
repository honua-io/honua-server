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

    [UnitTheory]
    [InlineData(false)]
    [InlineData(true)]
    [Operation(Operations.Export)]
    public async Task JobAccess_LegacyTenantlessJob_SingleTenantUpgradePreservesOwnership(bool priorFormat)
    {
        var tenant = new RequestTenantContext();
        var store = new InMemoryExecutionJobStore();
        var service = new TileExportJobService(TimeProvider.System, StorageOptions(),
            NullLogger<TileExportJobService>.Instance, store, new InMemoryJobQueue(), tenantContext: tenant);
        var principal = Principal(Owner);
        principal.Identities.First().AddClaim(new Claim("iss", "https://original.example"));
        var job = await service.SubmitAsync(CreatePlan(), null, null, principal, default);
        if (priorFormat)
        {
            await store.SetAsync(job with
            {
                Audit = job.Audit with
                {
                    RequestedBy = Owner,
                    SubmitterSecurityContext = job.Audit.SubmitterSecurityContext! with { OwnerActorId = null }
                }
            });
        }
        using var services = new ServiceCollection().AddSingleton<ITenantContext>(tenant).BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services, User = principal };
        await new TenantContextMiddleware(_ => Task.CompletedTask,
            Options.Create(new TenantContextOptions()), NullLogger<TenantContextMiddleware>.Instance).InvokeAsync(context);
        tenant.TenantId.Should().Be("public");
        (await service.GetStatusAsync(job.OperationId, ScopeFor(CreatePlan()), principal, default))
            .OperationId.Should().Be(job.OperationId);
        await FluentActions.Awaiting(() => service.GetResultAsync(job.OperationId, ScopeFor(CreatePlan()), principal, default))
            .Should().ThrowAsync<TileExportPreconditionFailedException>();
        foreach (var denied in new[] { Principal(Other), Principal(Owner) })
        {
            denied.Identities.First().AddClaim(new Claim("iss", "https://other.example"));
            await FluentActions.Awaiting(() => service.GetStatusAsync(job.OperationId, ScopeFor(CreatePlan()), denied, default))
                .Should().ThrowAsync<TileExportNotFoundException>();
        }
        await service.CancelAsync(job.OperationId, ScopeFor(CreatePlan()), principal, default);
        (await service.GetStatusAsync(job.OperationId, ScopeFor(CreatePlan()), principal, default))
            .Status.Should().Be(ExecutionJobStatus.Cancelled);
        tenant.Set("public", TenantContextSource.Claim);
        await FluentActions.Awaiting(() => service.GetStatusAsync(job.OperationId, ScopeFor(CreatePlan()), principal, default))
            .Should().ThrowAsync<TileExportNotFoundException>();
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

}
