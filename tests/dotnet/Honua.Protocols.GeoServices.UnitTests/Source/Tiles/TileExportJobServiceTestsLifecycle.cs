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
    public async Task GetResult_LegacyMapArtifact_RequiresResubmissionBeforeSigning()
    {
        var store = new InMemoryExecutionJobStore();
        var storage = Substitute.For<ICloudFileStorage>();
        storage.GetMetadataAsync("artifact-key", Arg.Any<CancellationToken>())
            .Returns(StoredArtifact("artifact-key", DateTimeOffset.UtcNow.AddHours(1), 4096));
        storage.GetPresignedUrlAsync("artifact-key", Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns("https://signed.example/artifact-key");
        var service = CreateService(store, new InMemoryJobQueue(), storage: storage);
        var plan = CreatePlan();
        plan = plan with { Source = ((TileExportMapSourceDescriptor)plan.Source) with { DataWatermark = "metadata-42" } };
        var job = await service.SubmitAsync(plan, null, null, Principal(Owner), default);
        await MarkSucceededAsync(store, job.OperationId, "artifact-key");

        await FluentActions.Awaiting(() => service.GetResultAsync(job.OperationId, ScopeFor(plan), Principal(Owner), default))
            .Should().ThrowAsync<TileExportPreconditionFailedException>();
        await storage.DidNotReceive().GetPresignedUrlAsync(Arg.Any<string>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>());
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

}
