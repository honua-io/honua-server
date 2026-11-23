// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Reflection;
using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Progress;
using Honua.Server.Features.Admin.Jobs;
using Honua.Server.Features.Admin.TileOperations;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Admin;

[Trait("Tier", "Fast")]
public sealed class AdminJobTenantScopeTests
{
    [Fact]
    public async Task GetDetailAsync_OtherTenantJob_ReturnsNull()
    {
        var (context, console) = await ConsoleScopeAsync("admin");

        (await console.GetDetailAsync(context, "job-a", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task CancelAsync_OtherTenantJob_ReturnsNullWithoutRequestingCancellation()
    {
        var (context, console) = await ConsoleScopeAsync("admin");

        (await console.CancelAsync(context, "job-a", CancellationToken.None)).Should().BeNull();
        (await context.RequestServices.GetRequiredService<IExecutionJobStore>().GetAsync("job-a"))!
            .CancellationRequestedAt.Should().BeNull();
    }

    [Fact]
    public async Task RetryAsync_OtherTenantJob_ReturnsNull()
    {
        var (context, console) = await ConsoleScopeAsync("admin");

        (await console.RetryAsync(context, "job-a", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task GetDiagnosticsAsync_PrincipalFromOtherTenant_ReturnsNull()
    {
        var (context, console) = await ConsoleScopeAsync("admin");

        var diagnostics = await console.GetDiagnosticsAsync(context.User, string.Empty, "job-a", null, null, CancellationToken.None);

        diagnostics.Should().BeNull();
    }

    [Theory]
    [InlineData("admin", "job-b")]
    [InlineData("platform_admin", "job-a,job-b")]
    public async Task ListAsync_MixedTenants_ReturnsJobsTheCallerMayRead(string role, string expected)
    {
        var (context, console) = await ConsoleScopeAsync(role);

        var listed = await console.ListAsync(context, new ConsoleJobQueryRequest(), CancellationToken.None);

        listed.Items.Select(item => item.JobId).Order().Should().Equal(expected.Split(','));
    }

    [Theory]
    [InlineData("HandleGetJobStatus")]
    [InlineData("HandleCancelJob")]
    public async Task HandleJobById_OtherTenantJob_ReturnsNotFound(string handler)
    {
        var (scope, tiles) = await TileScopeAsync("tenant-b");

        var result = await InvokeTile(handler, scope.Context("admin"), tiles);

        TenantScopeFixture.StatusOf(result).Should().Be(404);
        await tiles.DidNotReceiveWithAnyArgs().CancelAsync(default!, default);
    }

    [Fact]
    public async Task HandleRetryJob_OtherTenantJob_IsNotRetried()
    {
        var (scope, tiles) = await TileScopeAsync("tenant-b");

        var retry = await InvokeTile("HandleRetryJob", scope.Context("admin"), tiles);

        TenantScopeFixture.StatusOf(retry).Should().Be(400);
        await tiles.DidNotReceiveWithAnyArgs().RetryAsync(default!, default, default);
    }

    [Fact]
    public async Task HandleListJobs_OtherTenantJob_IsNotListed()
    {
        var (scope, tiles) = await TileScopeAsync("tenant-b");

        var list = await InvokeTile("HandleListJobs", scope.Context("admin"), tiles);

        ((TileOperationListResponse)((IValueHttpResult)list).Value!).Jobs.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleGetJobStatus_OwningTenant_ReturnsTheJob()
    {
        var (scope, tiles) = await TileScopeAsync("tenant-a");

        var result = await InvokeTile("HandleGetJobStatus", scope.Context("admin"), tiles);

        TenantScopeFixture.StatusOf(result).Should().Be(200);
    }

    [Fact]
    public void FromRequest_TenantBoundCaller_RecordsTheCallerTenant()
    {
        var context = new TenantScopeFixture("tenant-a").Context("admin");

        var submission = TileOperationSubmission.FromRequest(context, new TileOperationStartRequest { Operation = "seed" });

        submission.TenantId.Should().Be("tenant-a");
    }

    private static async Task<(HttpContext Context, ConsoleJobService Console)> ConsoleScopeAsync(string role)
    {
        var scope = new TenantScopeFixture("tenant-b");
        await scope.Jobs.TryCreateAsync(TenantScopeFixture.Job("job-a", TenantScopeFixture.AuditTenant("tenant-a")));
        await scope.Jobs.TryCreateAsync(TenantScopeFixture.Job("job-b", TenantScopeFixture.AuditTenant("tenant-b")));
        var context = scope.Context(role);
        return (context, Console(context.RequestServices));
    }

    private static async Task<(TenantScopeFixture Scope, ITileCacheJobService Tiles)> TileScopeAsync(string callerTenant)
    {
        var scope = new TenantScopeFixture(callerTenant);
        await scope.Jobs.TryCreateAsync(TenantScopeFixture.Job(
            "tile-a", TenantScopeFixture.AuditTenant("tenant-a"), ExecutionJobKind.TileCache));
        var progress = TileOperationProgress.CreateInitial("tile-a", "seed", "svc", 0, "WebMercatorQuad");
        var tiles = Substitute.For<ITileCacheJobService>();
        tiles.GetAsync("tile-a", Arg.Any<CancellationToken>()).Returns(progress);
        tiles.ListAsync(Arg.Any<TileOperationListScope>(), Arg.Any<CancellationToken>()).Returns([progress]);
        tiles.CancelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        tiles.RetryAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns("tile-retry");
        scope.TileJobs = tiles;
        return (scope, tiles);
    }

    private static async Task<IResult> InvokeTile(string handler, HttpContext context, ITileCacheJobService tiles)
    {
        var method = typeof(TileOperationsEndpoints).GetMethod(handler, BindingFlags.NonPublic | BindingFlags.Static)!;
        var args = method.GetParameters().Select(parameter => parameter.ParameterType switch
        {
            var type when type == typeof(string) => "tile-a",
            var type when type == typeof(HttpContext) => context,
            var type when type == typeof(ITileCacheJobService) => tiles,
            var type when type == typeof(bool) => true,
            var type when type == typeof(CancellationToken) => CancellationToken.None,
            _ => (object?)null,
        }).ToArray();
        return await (Task<IResult>)method.Invoke(null, args)!;
    }

    private static ConsoleJobService Console(IServiceProvider services)
    {
        var authorization = Substitute.For<IOperatorAuthorizationEvaluator>();
        authorization.EvaluateAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<OperatorAuthorizationRequest>(), Arg.Any<CancellationToken>())
            .Returns(AccessDecision.Allowed());
        var approval = Substitute.For<IOperatorApprovalEvaluator>();
        approval.Evaluate(Arg.Any<ClaimsPrincipal>(), Arg.Any<OperatorAuthorizationRequest>()).Returns(ApprovalRequirement.NotRequired());
        return new ConsoleJobService(services,
            new OperatorApprovalGate(authorization, approval, NullLogger<OperatorApprovalGate>.Instance),
            NullLogger<ConsoleJobService>.Instance, TimeProvider.System);
    }
}
