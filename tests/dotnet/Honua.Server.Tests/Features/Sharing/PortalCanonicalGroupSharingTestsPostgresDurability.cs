// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.Security.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Server.Features.Portal.Sharing;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Sharing;

/// <summary>Real PostgreSQL, independently reconstructed stores and fresh authorization requests.</summary>
[Collection("Database")]
[SecurityTest]
[Protocol(TestProtocols.FeatureServer)]
[Operation(Operations.Security)]
public sealed class PortalCanonicalGroupSharingTestsPostgresDurability(DatabaseFixtureAdapter database) : IAsyncLifetime
{
    private readonly string _schema = "portal6366_" + Guid.NewGuid().ToString("N");

    public async Task InitializeAsync()
    {
        await database.CreateSchemaUnderLockAsync(_schema);
        await using var stream = typeof(PostgresPortalGroupStore).Assembly.GetManifestResourceStream(
            "Honua.Server.Migrations.133_CreatePortalSharing.sql")
            ?? throw new InvalidOperationException("Portal sharing migration resource is missing.");
        using var reader = new StreamReader(stream);
        var migration = await reader.ReadToEndAsync();
        await database.ExecuteDdlUnderLockAsync(
            migration.Replace("$HonuaSchema$", _schema, StringComparison.Ordinal));
    }

    public Task DisposeAsync() => database.DropSchemaAsync(_schema);

    [IntegrationTest]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task AcceptedGroupsAndShares_SurviveReplicaReconstruction_AndRevokeFreshReads()
    {
        var groupsA = new PostgresPortalGroupStore(database.DataSource, TimeProvider.System, _schema);
        var groupsB = new PostgresPortalGroupStore(database.DataSource, TimeProvider.System, _schema);
        var accessorA = new TestHttpContextAccessor();
        var accessorB = new TestHttpContextAccessor();
        using var servicesA = Services(accessorA, groupsA, database.DataSource, _schema, "tenant-a");
        using var servicesB = Services(accessorB, groupsB, database.DataSource, _schema, "tenant-a");
        accessorA.HttpContext = Context(servicesA, "tenant-a", "member");
        accessorB.HttpContext = Context(servicesB, "tenant-a", "member");
        var sharesA = servicesA.GetRequiredService<IPortalItemSharingStore>();
        var sharesB = servicesB.GetRequiredService<IPortalItemSharingStore>();
        var audienceA = servicesA.GetRequiredService<IPortalAudienceSnapshotProvider>();
        var audienceB = servicesB.GetRequiredService<IPortalAudienceSnapshotProvider>();

        var group = await groupsA.CreateAsync(new PortalGroupRegistration("Crew", null, "private", [], "tenant-a"),
            "owner", CancellationToken.None);
        await groupsA.AddUsersAsync(group.Id, ["member"], CancellationToken.None);
        await sharesA.ShareAsync("service-6366", "owner", new PortalItemShareRequest(false, false, [group.Id]),
            CancellationToken.None);

        // B has no shared process state. It reads the records written by A and the
        // independently persisted audience grants a canonical service/resource read.
        (await groupsB.GetAsync(group.Id, CancellationToken.None))!.Members.Should().Contain("member");
        (await sharesB.GetAsync("service-6366", CancellationToken.None)).GroupIds.Should().Contain(group.Id);
        await AssertReadAsync(servicesB, accessorB.HttpContext!, audienceB, allowed: true);
        (await audienceA.GetServiceIdsAsync("outsider", "tenant-a", CancellationToken.None))
            .Should().NotContain("service-6366");
        (await audienceA.GetServiceIdsAsync("member", "tenant-b", CancellationToken.None))
            .Should().NotContain("service-6366");

        await groupsB.RemoveUsersAsync(group.Id, ["member"], CancellationToken.None);
        await AssertReadAsync(servicesA, accessorA.HttpContext!, audienceA, allowed: false);
        await groupsA.AddUsersAsync(group.Id, ["member"], CancellationToken.None);
        await sharesB.UnshareAsync("service-6366", new PortalItemShareRequest(false, false, [group.Id]),
            CancellationToken.None);
        await AssertReadAsync(servicesA, accessorA.HttpContext!, audienceA, allowed: false);

        // Row locking preserves both concurrent member additions across replicas.
        await Task.WhenAll(
            groupsA.AddUsersAsync(group.Id, ["first"], CancellationToken.None),
            groupsB.AddUsersAsync(group.Id, ["second"], CancellationToken.None));
        (await groupsA.GetAsync(group.Id, CancellationToken.None))!.Members
            .Should().Contain(["first", "second"]);

        var secondGroup = await groupsB.CreateAsync(
            new PortalGroupRegistration("Second crew", null, "private", [], "tenant-a"),
            "owner", CancellationToken.None);
        await groupsB.AddUsersAsync(secondGroup.Id, ["member"], CancellationToken.None);
        await Task.WhenAll(
            sharesA.ShareAsync("service-6366", "owner",
                new PortalItemShareRequest(false, false, [group.Id]), CancellationToken.None),
            sharesB.ShareAsync("service-6366", "owner",
                new PortalItemShareRequest(false, false, [secondGroup.Id]), CancellationToken.None));
        (await sharesA.GetAsync("service-6366", CancellationToken.None)).GroupIds
            .Should().Contain([group.Id, secondGroup.Id]);
        await sharesA.UnshareAsync("service-6366",
            new PortalItemShareRequest(false, false, [group.Id]), CancellationToken.None);
        await AssertReadAsync(servicesB, accessorB.HttpContext!, audienceB, allowed: true);
        await sharesB.UnshareAsync("service-6366",
            new PortalItemShareRequest(false, false, [secondGroup.Id]), CancellationToken.None);
        await AssertReadAsync(servicesA, accessorA.HttpContext!, audienceA, allowed: false);
    }

    private static ServiceProvider Services(
        IHttpContextAccessor accessor, IPortalGroupStore groups, Npgsql.NpgsqlDataSource dataSource,
        string schema, string tenantId)
    {
        var tenant = new TestTenantContext(tenantId);
        return new ServiceCollection()
            .AddSingleton(accessor)
            .AddSingleton<ITenantContext>(tenant)
            .AddSingleton(groups)
            .AddSingleton<IPortalItemSharingStore>(new PostgresPortalItemSharingStore(dataSource, accessor, schema))
            .AddSingleton<IPortalAudienceSnapshotProvider>(new PostgresPortalAudienceSnapshotProvider(dataSource, schema))
            .AddSingleton<IAccessPolicyEvaluator, AccessPolicyEvaluator>()
            .AddSingleton<IPortalResourceReadAuthorizer, PortalResourceReadAuthorizer>()
            .BuildServiceProvider();
    }

    private static DefaultHttpContext Context(IServiceProvider services, string tenant, string username)
        => new()
        {
            RequestServices = services,
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, username), new Claim("tenant_id", tenant)], "test")),
        };

    private static async Task AssertReadAsync(
        IServiceProvider services, HttpContext context, IPortalAudienceSnapshotProvider audience, bool allowed)
    {
        PortalAudienceSnapshot.Set(context,
            await audience.GetServiceIdsAsync("member", "tenant-a", CancellationToken.None));
        var service = new MetadataV2Service
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "service-6366", Name = "owned", Tenant = "tenant-a" },
            AccessPolicy = new AccessPolicy { AllowedRoles = ["private-service-reader"] },
            Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active },
        };
        var resource = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "resource-6366", Name = "layer", Tenant = "tenant-a" },
            AccessPolicy = new AccessPolicy { AllowedWriteRoles = ["editor"] },
            Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active },
        };
        var decision = AccessPolicyHelpers.EvaluateResourceAccess(context, resource, service);
        decision.IsAllowed.Should().Be(allowed);

        // The group grant never overrides an explicit resource read policy.
        var restricted = resource with { AccessPolicy = new AccessPolicy { AllowedRoles = ["layer-reader"] } };
        AccessPolicyHelpers.EvaluateResourceAccess(context, restricted, service).IsAllowed.Should().BeFalse();
    }

    private sealed class TestTenantContext(string tenantId) : ITenantContext
    {
        public string? TenantId => tenantId;
        public TenantContextSource Source => TenantContextSource.Claim;
        public bool RequireTenantId(out string value, out string? reason)
        {
            value = tenantId;
            reason = null;
            return true;
        }
    }

    private sealed class TestHttpContextAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }
}
