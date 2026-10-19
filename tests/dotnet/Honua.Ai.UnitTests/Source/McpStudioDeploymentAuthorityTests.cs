// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp.Resources;
using Honua.Core.Features.Authorization;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Deployment.Abstractions;
using Honua.Core.Features.Deployment.Domain;
using Honua.Core.Features.MultiTenancy;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.Publishing.Abstractions;
using Honua.Core.Features.Studio;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Domain;
using Honua.Core.Features.Studio.Services;
using Honua.Geoprocessing;
using Honua.Infrastructure.MultiTenancy;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

/// <summary>
/// Component controls for Studio-backed deployment resource projection and current authority.
/// These use the real Studio policy with controlled stores; they do not qualify publication,
/// database durability, an HTTP host, or an installed candidate.
/// </summary>
[Protocol(TestProtocols.Mcp)]
public sealed class McpStudioDeploymentAuthorityTests
{
    [UnitTest]
    public void StudioProjection_PreservesImmutableIdentityWithoutInventingPackageUri()
    {
        foreach (var family in new[] { StudioPackageFamily.Map, StudioPackageFamily.App })
        {
            var deployment = BuildDeployment("dep-source", "owner", "tenant-a", family);
            var view = DeploymentResource.ToView(deployment);

            view.SourceKind.Should().Be(nameof(DeploymentSourceKind.StudioPublishedVersion));
            view.SourceId.Should().Be(deployment.Source.StudioVersionId!.Value.ToString("D"));
            view.SourceResourceUri.Should().BeNull();
            view.StudioItemId.Should().Be(deployment.Source.StudioItemId);
            view.StudioVersionId.Should().Be(deployment.Source.StudioVersionId);
            view.StudioContentHash.Should().Be(deployment.Source.StudioContentHash);
            view.StudioOwnerId.Should().Be("owner");
            view.StudioPublicationRequestId.Should().Be(deployment.Source.StudioPublicationRequestId);
            view.StudioRollbackRequestId.Should().Be(deployment.Source.StudioRollbackRequestId);
            view.StudioFamily.Should().Be(family.ToString());
            view.RuntimeHealth.Should().Be(nameof(RuntimeHealth.Unknown));
            view.Provenance.PublishedServiceResourceUri.Should().BeNull();
        }
    }

    [UnitTest]
    [Endpoint("POST /mcp resources/read honua://deployments/{deploymentId}")]
    public async Task Detail_SameOwnerAndOperatorGrantCannotCrossTenant_EvenAsTenantAdmin()
    {
        var deployment = BuildDeployment("dep-private", "owner", "tenant-a");
        var pointers = Pointers(deployment, "owner", "tenant-a");
        using var allowed = new Request("owner", "tenant-a", [deployment], [pointers]);
        var result = await allowed.Detail.ReadAsync(allowed.Context, Uri(deployment), CancellationToken.None);
        var body = McpTestFactory.ParseJson(result.Contents.Single().Text);
        body.GetProperty("studioContentHash").GetString().Should().Be(deployment.Source.StudioContentHash);
        body.GetProperty("studioRollbackRequestId").GetGuid().Should().Be(deployment.Source.StudioRollbackRequestId!.Value);

        foreach (var role in new[] { "operator", "admin" })
        {
            using var denied = new Request("owner", "tenant-b", [deployment], [pointers], role);
            var act = () => denied.Detail.ReadAsync(denied.Context, Uri(deployment), CancellationToken.None);
            await act.Should().ThrowAsync<GeoprocessingNotFoundException>();
            await denied.Jobs.Received(1).EnsureCallerAuthorizedAsync(
                denied.Context.User, OperatorResourceType.Deployment, OperatorOperation.Read, CancellationToken.None);
        }
    }

    [UnitTest]
    [Endpoint("POST /mcp resources/read honua://deployments")]
    public async Task Index_FiltersOtherTenantsBeforeApplyingPageLimit()
    {
        var own = BuildDeployment("z-owned", "owner", "tenant-a");
        var hidden = Enumerable.Range(0, PromotionSurfaceIndexResource.DefaultPageSize + 1)
            .Select(index => BuildDeployment($"a-hidden-{index:D3}", "owner", "tenant-b"))
            .ToArray();
        var all = hidden.Append(own).ToArray();
        var pointers = all.Select(value => Pointers(value, "owner", value.Audit.TenantId!)).ToArray();
        using var request = new Request("owner", "tenant-a", all, pointers);

        var result = await request.Index.ReadAsync(request.Context, "honua://deployments", CancellationToken.None);
        var body = McpTestFactory.ParseJson(result.Contents.Single().Text);
        var ids = body.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("deploymentId").GetString()).ToArray();
        ids.Should().Equal("z-owned");
        body.GetProperty("truncated").GetBoolean().Should().BeFalse();
        await request.Jobs.Received(1).EnsureCallerAuthorizedAsync(
            request.Context.User, OperatorResourceType.Deployment, OperatorOperation.Read, CancellationToken.None);
    }

    [UnitTest]
    [Endpoint("POST /mcp resources/read honua://deployments/{deploymentId}")]
    public async Task Detail_OwnershipTransferUsesCurrentItemAuthorityForHistoricalVersion()
    {
        var deployment = BuildDeployment("dep-transferred", "old-owner", "tenant-a");
        var pointers = Pointers(deployment, "new-owner", "tenant-a") with
        {
            PublishedVersionId = Guid.NewGuid()
        };
        using var formerOwner = new Request("old-owner", "tenant-a", [deployment], [pointers]);
        var denied = () => formerOwner.Detail.ReadAsync(formerOwner.Context, Uri(deployment), CancellationToken.None);
        await denied.Should().ThrowAsync<GeoprocessingNotFoundException>();

        using var currentOwner = new Request("new-owner", "tenant-a", [deployment], [pointers]);
        var result = await currentOwner.Detail.ReadAsync(currentOwner.Context, Uri(deployment), CancellationToken.None);
        var body = McpTestFactory.ParseJson(result.Contents.Single().Text);
        body.GetProperty("deploymentId").GetString().Should().Be(deployment.DeploymentId);
        body.GetProperty("studioOwnerId").GetString().Should().Be("old-owner",
            "historical provenance stays unchanged while authority follows the current item");
    }

    [UnitTest]
    [Endpoint("POST /mcp resources/read honua://deployments/{deploymentId}")]
    [Endpoint("POST /mcp resources/read honua://deployments")]
    public async Task MissingCurrentItem_HidesDetailAndIndexDespiteHistoricalOwnership()
    {
        var deployment = BuildDeployment("dep-orphan", "owner", "tenant-a");
        using var request = new Request("owner", "tenant-a", [deployment], []);
        var act = () => request.Detail.ReadAsync(request.Context, Uri(deployment), CancellationToken.None);
        await act.Should().ThrowAsync<GeoprocessingNotFoundException>();

        var index = await request.Index.ReadAsync(request.Context, "honua://deployments", CancellationToken.None);
        McpTestFactory.ParseJson(index.Contents.Single().Text).GetProperty("items").GetArrayLength().Should().Be(0);
    }

    [UnitTest]
    [Endpoint("POST /mcp resources/read honua://deployments/{deploymentId}")]
    public async Task Detail_StudioOwnershipDoesNotReplaceDeploymentOperatorGrant()
    {
        var deployment = BuildDeployment("dep-gated", "owner", "tenant-a");
        using var request = new Request("owner", "tenant-a", [deployment], [Pointers(deployment, "owner", "tenant-a")]);
        request.Jobs.When(service => service.EnsureCallerAuthorizedAsync(
                Arg.Any<ClaimsPrincipal>(), OperatorResourceType.Deployment, OperatorOperation.Read,
                Arg.Any<CancellationToken>()))
            .Do(_ => throw new GeoprocessingAuthorizationException(requiresAuthentication: false));

        var act = () => request.Detail.ReadAsync(request.Context, Uri(deployment), CancellationToken.None);
        await act.Should().ThrowAsync<GeoprocessingAuthorizationException>();
        await request.Lifecycle.DidNotReceive().GetPointersAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    private static string Uri(Deployment deployment) => $"honua://deployments/{deployment.DeploymentId}";

    private static Deployment BuildDeployment(
        string id, string owner, string tenant, StudioPackageFamily family = StudioPackageFamily.Map)
        => Deployment.CreateDraft(id,
                DeploymentSource.FromStudioPublishedVersion(
                    Guid.NewGuid(), Guid.NewGuid(), new string('a', 64), owner, Guid.NewGuid(), family, Guid.NewGuid()),
                DeploymentTarget.ForStudioPublishedRoute($"/{id}"),
                audit: new OperationAuditInfo { RequestedBy = "approver", TenantId = tenant })
            .WithProvisioning().WithRollingOut().WithActive() with
        { Runtime = RuntimeState.Unknown() };

    private static StudioContentItemPointers Pointers(Deployment deployment, string owner, string tenant)
        => new()
        {
            ItemId = deployment.Source.StudioItemId!.Value,
            CurrentVersionId = deployment.Source.StudioVersionId,
            PublishedVersionId = deployment.Source.StudioVersionId,
            OwnerId = owner,
            TenantId = tenant
        };

    private sealed class Request : IDisposable
    {
        private readonly ServiceProvider _services;
        public DefaultHttpContext Context { get; }
        public IGeoprocessingJobService Jobs { get; } = Substitute.For<IGeoprocessingJobService>();
        public IStudioPackageLifecycleService Lifecycle { get; } = Substitute.For<IStudioPackageLifecycleService>();
        public DeploymentResource Detail { get; }
        public PromotionSurfaceIndexResource Index { get; }

        public Request(string user, string tenantId, Deployment[] deployments, StudioContentItemPointers[] items,
            string role = "operator")
        {
            var tenant = new RequestTenantContext();
            tenant.Set(tenantId, TenantContextSource.Claim);
            var pointers = items.ToDictionary(item => item.ItemId);
            Lifecycle.GetPointersAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                .Returns(call => Task.FromResult<StudioContentItemPointers?>(pointers.GetValueOrDefault(call.Arg<Guid>())));
            var store = Substitute.For<IDeploymentStore>();
            store.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(call => Task.FromResult(deployments.SingleOrDefault(item => item.DeploymentId == call.Arg<string>())));
            store.ListActiveAsync(Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyList<Deployment>>(deployments));
            var authorization = new StudioAuthorizationService(
                Substitute.For<IOperatorAuthorizationEvaluator>(), new OperatorScopeAuthorizer(),
                Monitor(new StudioEndUserAuthorizationOptions { Enabled = true }), Monitor(new AdminRoleOptions()),
                tenant, Monitor(new TenantIsolationOptions()));
            _services = new ServiceCollection()
                .AddSingleton<ITenantContext>(tenant)
                .AddSingleton(Lifecycle)
                .AddSingleton(Jobs)
                .AddSingleton(store)
                .AddSingleton(Substitute.For<IPublishedServiceStore>())
                .AddSingleton<IStudioAuthorizationService>(authorization)
                .AddSingleton<ILogger<DeploymentResource>>(NullLogger<DeploymentResource>.Instance)
                .AddSingleton<ILogger<PromotionSurfaceIndexResource>>(NullLogger<PromotionSurfaceIndexResource>.Instance)
                .BuildServiceProvider();
            Context = new DefaultHttpContext
            {
                RequestServices = _services,
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, user), new Claim(ClaimTypes.Name, user), new Claim(ClaimTypes.Role, role)],
                    "Test"))
            };
            Detail = ActivatorUtilities.CreateInstance<DeploymentResource>(_services);
            Index = ActivatorUtilities.CreateInstance<PromotionSurfaceIndexResource>(_services);
        }

        public void Dispose() => _services.Dispose();

        private static IOptionsMonitor<T> Monitor<T>(T value) where T : class
        {
            var monitor = Substitute.For<IOptionsMonitor<T>>();
            monitor.CurrentValue.Returns(value);
            return monitor;
        }
    }
}
