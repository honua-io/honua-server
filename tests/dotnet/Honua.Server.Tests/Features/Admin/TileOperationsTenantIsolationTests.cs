// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Reflection;
using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.MultiTenancy;
using Honua.Server.Features.Admin.TileOperations;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Admin;

[Trait("Tier", "Fast")]
[SecurityTest]
public sealed class TileOperationsTenantIsolationTests
{
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";
    private const string ServiceA = "svc-a";
    private const string ServiceB = "svc-b";
    private const int StorageLayerA = 101;
    private const int StorageLayerB = 202;

    [UnitTheory]
    [InlineData("purge")]
    [InlineData("invalidate")]
    public async Task HandleStartJob_TenantAdminGlobalUnscopedOperation_ReturnsForbiddenWithoutSubmitting(string operation)
    {
        var (context, jobs) = CreateContext(TenantA, "admin", multiTenancyEnabled: true);
        var request = new TileOperationStartRequest { Operation = operation };

        var result = await InvokeStartJobAsync(request, context, jobs);

        StatusOf(result).Should().Be(StatusCodes.Status403Forbidden);
        await jobs.DidNotReceiveWithAnyArgs().SubmitAsync(default!, default);
    }

    [UnitTheory]
    [InlineData("purge", ServiceB, 0)]
    [InlineData("invalidate", ServiceB, 0)]
    [InlineData("seed", ServiceB, 0)]
    [InlineData("warm", ServiceB, 0)]
    [InlineData("archive", ServiceB, 0)]
    [InlineData("publish", ServiceB, 0)]
    [InlineData("purge", ServiceB, null)]
    [InlineData("invalidate", null, StorageLayerB)]
    [InlineData("archive", null, StorageLayerB)]
    [InlineData("publish", null, StorageLayerB)]
    [InlineData("invalidate", null, 0)]
    public async Task HandleStartJob_TenantAdminTargetingForeignOrAmbiguousLayer_ReturnsNotFoundWithoutSubmitting(
        string operation,
        string? serviceId,
        int? layerId)
    {
        var (context, jobs) = CreateContext(TenantA, "admin", multiTenancyEnabled: true);
        var request = new TileOperationStartRequest
        {
            Operation = operation,
            ServiceId = serviceId,
            LayerId = layerId,
        };

        var result = await InvokeStartJobAsync(request, context, jobs);

        StatusOf(result).Should().Be(StatusCodes.Status404NotFound);
        await jobs.DidNotReceiveWithAnyArgs().SubmitAsync(default!, default);
    }

    [UnitTheory]
    [InlineData("purge", ServiceA, 0)]
    [InlineData("invalidate", ServiceA, null)]
    [InlineData("seed", ServiceA, 0)]
    [InlineData("archive", null, StorageLayerA)]
    [InlineData("publish", ServiceA, 0)]
    public async Task HandleStartJob_TenantAdminTargetingOwnLayer_ReturnsAccepted(
        string operation,
        string? serviceId,
        int? layerId)
    {
        var (context, jobs) = CreateContext(TenantA, "admin", multiTenancyEnabled: true);
        var request = new TileOperationStartRequest
        {
            Operation = operation,
            ServiceId = serviceId,
            LayerId = layerId,
        };

        var result = await InvokeStartJobAsync(request, context, jobs);

        StatusOf(result).Should().Be(StatusCodes.Status202Accepted);
        await jobs.Received(1).SubmitAsync(
            Arg.Is<TileOperationSubmission>(submission => submission.TenantId == TenantA),
            Arg.Any<CancellationToken>());
    }

    [UnitTheory]
    [InlineData("platform_admin", true)]
    [InlineData("admin", false)]
    public async Task HandleStartJob_PlatformAdminAndSingleTenantAdmin_AllowGlobalAndCrossTenantOperations(
        string role,
        bool multiTenancyEnabled)
    {
        var (context, jobs) = CreateContext(TenantA, role, multiTenancyEnabled);

        var globalPurge = await InvokeStartJobAsync(new TileOperationStartRequest { Operation = "purge" }, context, jobs);
        var crossTenantPublish = await InvokeStartJobAsync(
            new TileOperationStartRequest { Operation = "publish", ServiceId = ServiceB, LayerId = 0 },
            context,
            jobs);

        StatusOf(globalPurge).Should().Be(StatusCodes.Status202Accepted);
        StatusOf(crossTenantPublish).Should().Be(StatusCodes.Status202Accepted);
        await jobs.Received(2).SubmitAsync(Arg.Any<TileOperationSubmission>(), Arg.Any<CancellationToken>());
    }

    private static (DefaultHttpContext Context, ITileCacheJobService Jobs) CreateContext(
        string callerTenant,
        string role,
        bool multiTenancyEnabled)
    {
        var graph = BuildTwoTenantSnapshot();
        var graphProvider = Substitute.For<IMetadataV2GraphProvider>();
        graphProvider.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(graph);
        var jobs = Substitute.For<ITileCacheJobService>();
        jobs.SubmitAsync(Arg.Any<TileOperationSubmission>(), Arg.Any<CancellationToken>()).Returns("tile-job-1");
        var tenant = Substitute.For<ITenantContext>();
        tenant.TenantId.Returns(callerTenant);
        tenant.Source.Returns(multiTenancyEnabled ? TenantContextSource.Claim : TenantContextSource.Default);

        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton(graphProvider)
            .AddSingleton(jobs)
            .AddSingleton(tenant)
            .AddSingleton(Options.Create(new LimitsOptions()))
            .AddSingleton(Options.Create(new TenantContextOptions
            {
                Enabled = multiTenancyEnabled,
                DefaultTenantId = "public",
            }))
            .BuildServiceProvider();

        var context = new DefaultHttpContext
        {
            RequestServices = services,
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "operator"), new Claim(ClaimTypes.Role, role)],
                "Test")),
        };
        return (context, jobs);
    }

    private static MetadataV2GraphSnapshot BuildTwoTenantSnapshot()
    {
        var serviceA = CreateService(ServiceA, TenantA);
        var serviceB = CreateService(ServiceB, TenantB);
        var resourceA = CreateResource("res-a", "bind-a", TenantA);
        var resourceB = CreateResource("res-b", "bind-b", TenantB);
        var bindingA = CreateBinding("bind-a", "res-a", StorageLayerA, TenantA);
        var bindingB = CreateBinding("bind-b", "res-b", StorageLayerB, TenantB);
        var pubA = CreatePublication("pub-a", ServiceA, "res-a", "bind-a", TenantA);
        var pubB = CreatePublication("pub-b", ServiceB, "res-b", "bind-b", TenantB);

        return new MetadataV2GraphSnapshot(
            new MetadataV2Graph
            {
                Revision = 1,
                Services = [serviceA, serviceB],
                Resources = [resourceA, resourceB],
                StorageBindings = [bindingA, bindingB],
                Publications = [pubA, pubB],
            },
            "\"tile-tenant-isolation\"",
            DateTimeOffset.UnixEpoch);
    }

    private static MetadataV2Service CreateService(string id, string tenant) => new()
    {
        Metadata = new MetadataV2ObjectMetadata { Id = id, Name = id, Tenant = tenant },
        Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active },
    };

    private static MetadataV2Object CreateResource(string id, string bindingId, string tenant) => new()
    {
        Metadata = new MetadataV2ObjectMetadata { Id = id, Name = id, Tenant = tenant },
        StorageBindingIds = [bindingId],
        PrimaryStorageBindingId = bindingId,
        Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active },
    };

    private static MetadataV2StorageBinding CreateBinding(string id, string resourceId, int storageLayerId, string tenant) => new()
    {
        Metadata = new MetadataV2ObjectMetadata { Id = id, Name = id, Tenant = tenant },
        ResourceId = resourceId,
        StorageLayerId = storageLayerId,
        Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active },
    };

    private static MetadataV2Layer CreatePublication(
        string id, string serviceId, string resourceId, string bindingId, string tenant) => new()
        {
            Metadata = new MetadataV2ObjectMetadata { Id = id, Name = id, Tenant = tenant },
            ServiceId = serviceId,
            ResourceId = resourceId,
            StorageBindingId = bindingId,
            LayerIndex = 0,
            Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active },
        };

    private static async Task<IResult> InvokeStartJobAsync(
        TileOperationStartRequest request,
        DefaultHttpContext context,
        ITileCacheJobService jobs)
    {
        var method = typeof(TileOperationsEndpoints).GetMethod("HandleStartJob", BindingFlags.NonPublic | BindingFlags.Static)!;
        var task = (Task<Results<JsonHttpResult<TileOperationStartResponse>, JsonHttpResult<ProblemDetailsResponse>>>)
            method.Invoke(null, [request, context, jobs, CancellationToken.None])!;
        var result = await task;
        return result.Result;
    }

    private static int StatusOf(IResult result) => (result as IStatusCodeHttpResult)?.StatusCode ?? 200;
}
