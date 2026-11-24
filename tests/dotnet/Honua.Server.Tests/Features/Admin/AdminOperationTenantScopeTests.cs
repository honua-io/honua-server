// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Reflection;
using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.Orchestration.Domain;
using Honua.Infrastructure.MultiTenancy;
using Honua.Infrastructure.Progress;
using Honua.Migration;
using Honua.Server.Features.Admin;
using Honua.Server.Features.Admin.TileOperations;
using Honua.TestKit.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Admin;

[Trait("Tier", "Fast")]
public sealed class AdminOperationTenantScopeTests
{
    [Theory]
    [InlineData("enabled:denied", "absent", "admin")]
    [InlineData("enabled:denied", "blank", "admin")]
    [InlineData("enabled:allowed", "absent", "platform_admin")]
    [InlineData("enabled:allowed", "blank", "platform_admin")]
    [InlineData("disabled:allowed", "absent", "admin")]
    [InlineData("disabled:allowed", "blank", "admin")]
    public void CanAccess_UnownedOperation_UsesDeploymentPolicy(
        string deploymentCase, string tenantService, string role)
    {
        var services = new ServiceCollection()
            .AddSingleton(Options.Create(new TenantContextOptions { Enabled = deploymentCase.StartsWith("enabled", StringComparison.Ordinal) }));
        if (tenantService == "blank")
        {
            var tenant = Substitute.For<ITenantContext>();
            tenant.TenantId.Returns(string.Empty);
            services.AddSingleton(tenant);
        }

        using var provider = services.BuildServiceProvider();
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, role)], "Test"));

        OperationTenantAuthorization.CanAccess(user, provider, null).Should().Be(deploymentCase.EndsWith("allowed", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("tenant-a", "admin", 200)]
    [InlineData("tenant-b", "admin", 404)]
    [InlineData("tenant-b", "platform_admin", 200)]
    public async Task HandleGetOperationStatus_CallerTenantAndRole_ChecksOwningTenant(string callerTenant, string role, int expected)
    {
        var scope = new TenantScopeFixture(callerTenant);
        await scope.AddUploadAsync("op-a", TenantScopeFixture.AuditTenant("tenant-a"));

        var result = await scope.GetStatusAsync("op-a", role);

        TenantScopeFixture.StatusOf(result).Should().Be(expected);
    }

    [Fact]
    public async Task HandleGetOperationStatus_SubmitterTenantOnly_HidesOtherTenantOperation()
    {
        var scope = new TenantScopeFixture("tenant-b");
        await scope.AddUploadAsync("op-a", new OperationAuditInfo
        {
            SubmitterSecurityContext = new JobSecurityContext("alice", "tenant-a", Array.Empty<JobSecurityClaim>())
        });

        TenantScopeFixture.StatusOf(await scope.GetStatusAsync("op-a", "admin")).Should().Be(404);
    }

    [Fact]
    public async Task HandleGetOperationStatus_PrintProgressWithoutJob_UsesRecordedTenant()
    {
        var scope = new TenantScopeFixture("tenant-b");
        var print = PrintProgress.CreateInitial("print-a", "pdf", "A4", 1) with { TenantId = "tenant-a" };
        await scope.Progress.SetProgressAsync("print-a", print, TimeSpan.FromMinutes(5));

        TenantScopeFixture.StatusOf(await scope.GetStatusAsync("print-a", "admin")).Should().Be(404);
        TenantScopeFixture.StatusOf(await scope.GetStatusAsync("print-a", "platform_admin")).Should().Be(200);
    }

    [Theory]
    [InlineData("tenant-a", 200)]
    [InlineData("tenant-b", 404)]
    public async Task HandleGetOperationStatus_LayerPublishProgressWithoutJob_UsesRecordedTenant(string callerTenant, int expected)
    {
        var scope = new TenantScopeFixture(callerTenant);
        var publish = new LayerPublishJobProgress
        {
            OperationId = "publish-a",
            Status = OperationStatus.Processing,
            StartedAt = DateTimeOffset.UtcNow,
            TenantId = "tenant-a"
        };
        await scope.Progress.SetProgressAsync("publish-a", publish, TimeSpan.FromMinutes(5));

        TenantScopeFixture.StatusOf(await scope.GetStatusAsync("publish-a", "admin")).Should().Be(expected);
    }

    [Theory]
    [InlineData("tenant-a", 200)]
    [InlineData("tenant-b", 404)]
    public async Task HandleGetOperationStatus_WorkflowProgressWithoutJob_UsesRecordedTenant(string callerTenant, int expected)
    {
        var scope = new TenantScopeFixture(callerTenant);
        var run = WorkflowProgress.CreateForPendingRun("run-a", "workflow-a", 2, DateTimeOffset.UtcNow) with { TenantId = "tenant-a" };
        await scope.Progress.SetProgressAsync("run-a", run, TimeSpan.FromMinutes(5));

        TenantScopeFixture.StatusOf(await scope.GetStatusAsync("run-a", "admin")).Should().Be(expected);
    }

    [Theory]
    [InlineData("admin", 404)]
    [InlineData("platform_admin", 200)]
    public async Task HandleGetOperationStatus_UnownedOperationWithTenancy_VisibleOnlyToCrossTenantRole(string role, int expected)
    {
        var scope = new TenantScopeFixture("public");
        await scope.AddUploadAsync("op-legacy", new OperationAuditInfo());

        TenantScopeFixture.StatusOf(await scope.GetStatusAsync("op-legacy", role)).Should().Be(expected);
    }

    [Fact]
    public async Task HandleGetOperationStatus_UnownedOperationInSingleTenantDeployment_StaysVisible()
    {
        var scope = new TenantScopeFixture("public", TenantContextSource.Default);
        await scope.AddUploadAsync("op-legacy", new OperationAuditInfo());

        TenantScopeFixture.StatusOf(await scope.GetStatusAsync("op-legacy", "admin")).Should().Be(200);
    }

    [Fact]
    public async Task HandleListOperations_MixedTenants_ReturnOnlyCallerTenantOperations()
    {
        var scope = new TenantScopeFixture("tenant-a");
        await scope.AddUploadAsync("op-a", TenantScopeFixture.AuditTenant("tenant-a"));
        await scope.AddUploadAsync("op-b", TenantScopeFixture.AuditTenant("tenant-b"));
        var context = scope.Context("admin");

        var active = await OperationsProgressEndpoints.HandleListActiveOperations(context, null, CancellationToken.None);
        var byType = await OperationsProgressEndpoints.HandleGetOperationsByType("Upload", context, CancellationToken.None);

        OperationIds(active).Should().Equal("op-a");
        OperationIds(byType).Should().Equal("op-a");
    }

    [Fact]
    public async Task HandleCancelOperation_OtherTenant_ReturnsNotFoundAndLeavesOperationRunning()
    {
        var scope = new TenantScopeFixture("tenant-b");
        await scope.AddUploadAsync("op-a", TenantScopeFixture.AuditTenant("tenant-a"));

        var result = await scope.CancelAsync("op-a", "admin");

        TenantScopeFixture.StatusOf(result).Should().Be(404);
        (await scope.Progress.GetProgressAsync("op-a"))!.Status.Should().Be(OperationStatus.Processing);
        (await scope.Jobs.GetAsync("op-a"))!.CancellationRequestedAt.Should().BeNull();
    }

    private static string[] OperationIds(IResult result)
    {
        var operations = ((IValueHttpResult)result).Value switch
        {
            ActiveOperationsResponse active => active.Operations,
            OperationsByTypeResponse byType => byType.Operations,
            var other => throw new InvalidOperationException($"Unexpected list response {other?.GetType().Name}."),
        };
        return operations.Select(operation => operation.GetProperty("operationId").GetString()!).ToArray();
    }
}
