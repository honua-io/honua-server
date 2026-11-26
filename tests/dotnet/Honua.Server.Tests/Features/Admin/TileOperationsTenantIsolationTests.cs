// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Reflection;
using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.Metadata.Abstractions;
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
    private readonly record struct CallerIdentity(string TenantId, string Role, TenantContextSource Source);

    [UnitTheory]
    [InlineData("purge")]
    [InlineData("invalidate")]
    public async Task HandleStartJob_TenantAdminGlobalUnscopedOperation_ReturnsForbiddenWithoutSubmitting(string operation)
    {
        var (context, jobs) = CreateContext(new CallerIdentity(TwoTenantGraphFixture.TenantA, "admin", TenantContextSource.Claim));
        var request = new TileOperationStartRequest { Operation = operation };

        var result = await InvokeStartJobAsync(request, context, jobs);

        StatusOf(result).Should().Be(StatusCodes.Status403Forbidden);
        await jobs.DidNotReceiveWithAnyArgs().SubmitAsync(default!, default);
    }

    [UnitTheory]
    [InlineData("purge", TwoTenantGraphFixture.ServiceB, 0)]
    [InlineData("invalidate", TwoTenantGraphFixture.ServiceB, 0)]
    [InlineData("seed", TwoTenantGraphFixture.ServiceB, 0)]
    [InlineData("warm", TwoTenantGraphFixture.ServiceB, 0)]
    [InlineData("archive", TwoTenantGraphFixture.ServiceB, 0)]
    [InlineData("publish", TwoTenantGraphFixture.ServiceB, 0)]
    [InlineData("purge", TwoTenantGraphFixture.ServiceB, null)]
    [InlineData("invalidate", null, TwoTenantGraphFixture.StorageLayerB)]
    [InlineData("archive", null, TwoTenantGraphFixture.StorageLayerB)]
    [InlineData("publish", null, TwoTenantGraphFixture.StorageLayerB)]
    [InlineData("invalidate", null, 0)]
    public async Task HandleStartJob_TenantAdminTargetingForeignOrAmbiguousLayer_ReturnsNotFoundWithoutSubmitting(
        string operation,
        string? serviceId,
        int? layerId)
    {
        var (context, jobs) = CreateContext(new CallerIdentity(TwoTenantGraphFixture.TenantA, "admin", TenantContextSource.Claim));
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
    [InlineData("purge", TwoTenantGraphFixture.ServiceA, 0)]
    [InlineData("invalidate", TwoTenantGraphFixture.ServiceA, null)]
    [InlineData("seed", TwoTenantGraphFixture.ServiceA, 0)]
    [InlineData("archive", null, TwoTenantGraphFixture.StorageLayerA)]
    [InlineData("publish", TwoTenantGraphFixture.ServiceA, 0)]
    public async Task HandleStartJob_TenantAdminTargetingOwnLayer_ReturnsAccepted(
        string operation,
        string? serviceId,
        int? layerId)
    {
        var (context, jobs) = CreateContext(new CallerIdentity(TwoTenantGraphFixture.TenantA, "admin", TenantContextSource.Claim));
        var request = new TileOperationStartRequest
        {
            Operation = operation,
            ServiceId = serviceId,
            LayerId = layerId,
        };

        var result = await InvokeStartJobAsync(request, context, jobs);

        StatusOf(result).Should().Be(StatusCodes.Status202Accepted);
        await jobs.Received(1).SubmitAsync(
            Arg.Is<TileOperationSubmission>(submission => submission.TenantId == TwoTenantGraphFixture.TenantA),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task HandleStartJob_PlatformAdmin_AllowsGlobalAndCrossTenantOperations()
        => await AssertGlobalAndCrossTenantAllowedAsync(
            new CallerIdentity(TwoTenantGraphFixture.TenantA, "platform_admin", TenantContextSource.Claim));

    [UnitTest]
    public async Task HandleStartJob_SingleTenantAdmin_AllowsGlobalAndCrossTenantOperations()
        => await AssertGlobalAndCrossTenantAllowedAsync(
            new CallerIdentity(TwoTenantGraphFixture.TenantA, "admin", TenantContextSource.Default));

    private static async Task AssertGlobalAndCrossTenantAllowedAsync(CallerIdentity caller)
    {
        var (context, jobs) = CreateContext(caller);

        var globalPurge = await InvokeStartJobAsync(new TileOperationStartRequest { Operation = "purge" }, context, jobs);
        var crossTenantPublish = await InvokeStartJobAsync(
            new TileOperationStartRequest { Operation = "publish", ServiceId = TwoTenantGraphFixture.ServiceB, LayerId = 0 },
            context,
            jobs);

        StatusOf(globalPurge).Should().Be(StatusCodes.Status202Accepted);
        StatusOf(crossTenantPublish).Should().Be(StatusCodes.Status202Accepted);
        await jobs.Received(2).SubmitAsync(Arg.Any<TileOperationSubmission>(), Arg.Any<CancellationToken>());
    }

    private static (DefaultHttpContext Context, ITileCacheJobService Jobs) CreateContext(CallerIdentity caller)
    {
        var graphProvider = Substitute.For<IMetadataV2GraphProvider>();
        graphProvider.GetCurrentAsync(Arg.Any<CancellationToken>())
            .Returns(TwoTenantGraphFixture.BuildSnapshot("\"tile-tenant-isolation\""));
        var jobs = Substitute.For<ITileCacheJobService>();
        jobs.SubmitAsync(Arg.Any<TileOperationSubmission>(), Arg.Any<CancellationToken>()).Returns("tile-job-1");
        var tenant = Substitute.For<ITenantContext>();
        tenant.TenantId.Returns(caller.TenantId);
        tenant.Source.Returns(caller.Source);

        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton(graphProvider)
            .AddSingleton(jobs)
            .AddSingleton(tenant)
            .AddSingleton(Options.Create(new LimitsOptions()))
            .AddSingleton(Options.Create(new TenantContextOptions
            {
                Enabled = caller.Source != TenantContextSource.Default,
                DefaultTenantId = "public",
            }))
            .BuildServiceProvider();

        var context = new DefaultHttpContext
        {
            RequestServices = services,
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "operator"), new Claim(ClaimTypes.Role, caller.Role)],
                "Test")),
        };
        return (context, jobs);
    }

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
