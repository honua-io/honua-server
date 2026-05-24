// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Console.Domain;
using Honua.Server.Features.Admin.Services;
using Honua.Server.Features.Console.Services;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Console;

/// <summary>
/// Verifies Console RBAC evaluator decisions across the 7 Console verbs and the
/// route entitlement surface. The evaluator is server-authored, so these tests
/// guard the canonical decision matrix.
/// </summary>
public class ConsoleActionEvaluatorTests
{
    private static readonly IOptions<RbacOptions> RbacOpts = Options.Create(new RbacOptions());

    private static ConsoleActionEvaluator BuildEvaluator(IRoleStore? store = null)
    {
        store ??= new InMemoryRoleStore();
        return new ConsoleActionEvaluator(store, RbacOpts, NullLogger<ConsoleActionEvaluator>.Instance);
    }

    private static ClaimsPrincipal AdminPrincipal()
    {
        var identity = new ClaimsIdentity(authenticationType: "Test");
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, "admin-user"));
        identity.AddClaim(new Claim(ClaimTypes.Role, "admin"));
        return new ClaimsPrincipal(identity);
    }

    private static ClaimsPrincipal ViewerPrincipal()
    {
        var identity = new ClaimsIdentity(authenticationType: "Test");
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, "viewer-user"));
        identity.AddClaim(new Claim(ClaimTypes.Role, "viewer"));
        return new ClaimsPrincipal(identity);
    }

    private static ClaimsPrincipal Anonymous() => new(new ClaimsIdentity());

    private static ConsoleContentItem Sample(ConsoleVisibility visibility = ConsoleVisibility.Organization, string owner = "other-user", ConsoleContentItemType type = ConsoleContentItemType.SavedMap)
    {
        return new ConsoleContentItem
        {
            Id = "item",
            Name = "sample",
            ItemType = type,
            Visibility = visibility,
            OwnerId = owner,
        };
    }

    [UnitTest]
    public async Task Admin_HasAllActions_OnEveryItem()
    {
        var evaluator = BuildEvaluator();
        var actions = await evaluator.EvaluateItemActionsAsync(AdminPrincipal(), Sample(), Array.Empty<ConsoleContentAction>(), CancellationToken.None);

        Assert.Contains(ConsoleContentAction.View, actions);
        Assert.Contains(ConsoleContentAction.Edit, actions);
        Assert.Contains(ConsoleContentAction.Publish, actions);
        Assert.Contains(ConsoleContentAction.Share, actions);
        Assert.Contains(ConsoleContentAction.Embed, actions);
        Assert.Contains(ConsoleContentAction.Operate, actions);
        Assert.Contains(ConsoleContentAction.Administer, actions);
    }

    [UnitTest]
    public async Task Viewer_OnOrganizationItem_CanViewAndOperate_ButNotEditOrAdminister()
    {
        var evaluator = BuildEvaluator();
        var actions = await evaluator.EvaluateItemActionsAsync(ViewerPrincipal(), Sample(), Array.Empty<ConsoleContentAction>(), CancellationToken.None);

        Assert.Contains(ConsoleContentAction.View, actions);
        Assert.Contains(ConsoleContentAction.Operate, actions);
        Assert.DoesNotContain(ConsoleContentAction.Edit, actions);
        Assert.DoesNotContain(ConsoleContentAction.Administer, actions);
        Assert.DoesNotContain(ConsoleContentAction.Publish, actions);
    }

    [UnitTest]
    public async Task Owner_OfPersonalItem_CanViewEditShare()
    {
        var evaluator = BuildEvaluator();
        var identity = new ClaimsIdentity(authenticationType: "Test");
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, "owner-user"));
        identity.AddClaim(new Claim(ClaimTypes.Role, "viewer"));
        var principal = new ClaimsPrincipal(identity);

        var item = Sample(visibility: ConsoleVisibility.Personal, owner: "owner-user");
        var actions = await evaluator.EvaluateItemActionsAsync(principal, item, Array.Empty<ConsoleContentAction>(), CancellationToken.None);

        Assert.Contains(ConsoleContentAction.View, actions);
        Assert.Contains(ConsoleContentAction.Edit, actions);
        Assert.Contains(ConsoleContentAction.Share, actions);
    }

    [UnitTest]
    public async Task Anonymous_OnPublicItem_CanView()
    {
        var evaluator = BuildEvaluator();
        var actions = await evaluator.EvaluateItemActionsAsync(Anonymous(), Sample(visibility: ConsoleVisibility.Public), Array.Empty<ConsoleContentAction>(), CancellationToken.None);

        Assert.Contains(ConsoleContentAction.View, actions);
        Assert.DoesNotContain(ConsoleContentAction.Edit, actions);
        Assert.DoesNotContain(ConsoleContentAction.Administer, actions);
    }

    [UnitTest]
    public async Task Anonymous_OnPersonalItem_HasNoActions()
    {
        var evaluator = BuildEvaluator();
        var actions = await evaluator.EvaluateItemActionsAsync(Anonymous(), Sample(visibility: ConsoleVisibility.Personal), Array.Empty<ConsoleContentAction>(), CancellationToken.None);

        Assert.Empty(actions);
    }

    [UnitTest]
    public async Task RouteEntitlements_AdminAllowsAllRoutes()
    {
        var evaluator = BuildEvaluator();
        var entitlements = await evaluator.EvaluateRouteEntitlementsAsync(AdminPrincipal(), ConsoleActionEvaluator.DefaultRouteKeys, CancellationToken.None);

        foreach (var entitlement in entitlements)
        {
            Assert.True(entitlement.Allowed, $"Admin should be allowed on '{entitlement.RouteKey}'");
            Assert.Null(entitlement.Reason);
        }
    }

    private static readonly string[] AdminAndCatalogRoutes = { "admin", "catalog" };

    [UnitTest]
    public async Task RouteEntitlements_ViewerCannotAccessAdmin()
    {
        var evaluator = BuildEvaluator();
        var entitlements = await evaluator.EvaluateRouteEntitlementsAsync(ViewerPrincipal(), AdminAndCatalogRoutes, CancellationToken.None);

        var admin = entitlements.Single(e => e.RouteKey == "admin");
        var catalog = entitlements.Single(e => e.RouteKey == "catalog");

        Assert.False(admin.Allowed);
        Assert.Equal("insufficient-capability", admin.Reason);
        Assert.True(catalog.Allowed);
    }

    [UnitTest]
    public async Task BulkItemEvaluation_KeyedByItemId()
    {
        var evaluator = BuildEvaluator();
        var items = new[]
        {
            Sample(visibility: ConsoleVisibility.Public) with { Id = "pub" },
            Sample(visibility: ConsoleVisibility.Personal, owner: "viewer-user") with { Id = "self" },
        };

        var actions = await evaluator.EvaluateItemActionsAsync(ViewerPrincipal(), items, Array.Empty<ConsoleContentAction>(), CancellationToken.None);

        Assert.True(actions.ContainsKey("pub"));
        Assert.True(actions.ContainsKey("self"));
        Assert.Contains(ConsoleContentAction.View, actions["pub"]);
        Assert.Contains(ConsoleContentAction.Edit, actions["self"]);
    }

    [UnitTest]
    public async Task Capabilities_AdminGetsCatalogPublish()
    {
        var evaluator = BuildEvaluator();
        var capabilities = await evaluator.ResolveCapabilitiesAsync(AdminPrincipal(), CancellationToken.None);

        Assert.Contains("admin.rbac.write", capabilities);
        Assert.Contains("catalog.publish", capabilities);
        Assert.Contains("metadata.write", capabilities);
    }

    [UnitTest]
    public async Task Capabilities_ViewerOnlyReceivesReadCapabilities()
    {
        var evaluator = BuildEvaluator();
        var capabilities = await evaluator.ResolveCapabilitiesAsync(ViewerPrincipal(), CancellationToken.None);

        Assert.Contains("metadata.read", capabilities);
        Assert.Contains("catalog.read", capabilities);
        Assert.DoesNotContain("metadata.write", capabilities);
        Assert.DoesNotContain("admin.rbac.write", capabilities);
    }
}
