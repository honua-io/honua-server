// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Infrastructure.Authentication;
using Honua.Server.Features.Admin;
using Honua.Server.Features.Operations;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Admin;

[Protocol(TestProtocols.Admin)]
[Operation(Operations.Configuration)]
public sealed class AgentRouteAdmissionMiddlewareTests
{
    private const string AdminRoute = "/api/v{version:apiVersion}/admin/connections/{id:guid}";

    [UnitTheory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public void IsRefused_AgentChangingAnAdminRoute_ReturnsTrue(string method)
        => AgentRouteAdmissionMiddleware.IsRefused(Context(method, AdminRoute, Agent())).Should().BeTrue();

    [UnitTheory]
    [InlineData("/api/v1/admin/import/geoservices/start")]
    [InlineData("/api/v{version:apiVersion}/console/content/{id}/share/link")]
    [InlineData("/api/V{version:apiversion}/Admin")]
    [InlineData("api/v{version:apiVersion}/admin/rate-limits")]
    public void IsRefused_AgentChangingAnyAdminOrConsoleRoute_ReturnsTrue(string route)
        => AgentRouteAdmissionMiddleware.IsRefused(Context("POST", route, Agent())).Should().BeTrue();

    [UnitTheory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    public void IsRefused_AgentReading_ReturnsFalse(string method)
        => AgentRouteAdmissionMiddleware.IsRefused(Context(method, AdminRoute, Agent())).Should().BeFalse();

    [UnitTheory]
    [InlineData("/api/v{version:apiVersion}/operations/{operationId}/submit")]
    [InlineData("/api/v{version:apiVersion}/administration")]
    [InlineData("/mcp")]
    [InlineData("/rest/services/{service}/FeatureServer/{layer}/applyEdits")]
    public void IsRefused_AgentOnARouteWithoutAdministratorAuthority_ReturnsFalse(string route)
        => AgentRouteAdmissionMiddleware.IsRefused(Context("POST", route, Agent())).Should().BeFalse();

    [UnitTheory]
    [InlineData("Admin")]
    [InlineData("AdminCredentialManagement")]
    [InlineData("OpsRead")]
    public void IsRefused_AgentChangingARouteThatRequiresAdministratorAuthority_ReturnsTrue(string policy)
        => AgentRouteAdmissionMiddleware.IsRefused(Context("DELETE", Endpoint("/ogc/styles/{styleId}", new AuthorizeAttribute(policy)), Agent()))
            .Should().BeTrue();

    [UnitTest]
    public void IsRefused_AgentChangingARouteWithAnotherPolicy_ReturnsFalse()
        => AgentRouteAdmissionMiddleware.IsRefused(Context("POST", Endpoint("/api/v{version:apiVersion}/studio/package-drafts", new AuthorizeAttribute("StudioLifecycle")), Agent()))
            .Should().BeFalse();

    [UnitTest]
    public void IsRefused_AgentMarkedByRole_ReturnsTrue()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Role, Honua.Core.Features.Operations.Policy.AgentApprovalRule.AgentRole)], "test"));

        AgentRouteAdmissionMiddleware.IsRefused(Context("POST", AdminRoute, principal)).Should().BeTrue();
    }

    [UnitTest]
    public void IsRefused_PersonChangingAnAdminRoute_ReturnsFalse()
    {
        var person = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "administrator")], "test"));

        AgentRouteAdmissionMiddleware.IsRefused(Context("POST", AdminRoute, person)).Should().BeFalse();
        AgentRouteAdmissionMiddleware.IsRefused(Context("POST", AdminRoute, new ClaimsPrincipal(new ClaimsIdentity()))).Should().BeFalse();
    }

    [UnitTest]
    public void IsRefused_RouteWithItsOwnAgentRule_ReturnsFalse()
    {
        var context = Context("POST", Endpoint(AdminRoute, new RouteAgentRuleMetadata("decides by audience")), Agent());

        AgentRouteAdmissionMiddleware.IsRefused(context).Should().BeFalse();
    }

    [UnitTest]
    public void IsRefused_RequestDispatchedByTheOperationsGateway_ReturnsFalse()
    {
        var context = Context("POST", AdminRoute, Agent());
        context.Features.Set(AttestedOperationLineage.Instance);

        AgentRouteAdmissionMiddleware.IsRefused(context).Should().BeFalse();
    }

    [UnitTest]
    public void IsRefused_RequestWithoutARouteEndpoint_ReturnsFalse()
    {
        var context = new DefaultHttpContext { User = Agent() };
        context.Request.Method = HttpMethods.Post;

        AgentRouteAdmissionMiddleware.IsRefused(context).Should().BeFalse();
    }

    [UnitTest]
    public async Task InvokeAsync_AgentChangingAnAdminRoute_WritesTheApprovalRequiredProblem()
    {
        var context = Context("DELETE", AdminRoute, Agent());
        context.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        context.Response.Body = new MemoryStream();
        var ran = false;

        await new AgentRouteAdmissionMiddleware(_ => { ran = true; return Task.CompletedTask; }).InvokeAsync(context);

        ran.Should().BeFalse();
        context.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body);
        (await reader.ReadToEndAsync()).Should().Contain(AgentRouteAdmissionMiddleware.PolicyRef);
    }

    private static ClaimsPrincipal Agent()
        => new(new ClaimsIdentity([new Claim(AdminApiKeyPermission.PermissionClaimType, AgentPrincipal.AgentGrant)], "test"));

    private static DefaultHttpContext Context(string method, string route, ClaimsPrincipal user)
        => Context(method, Endpoint(route), user);

    private static DefaultHttpContext Context(string method, RouteEndpoint endpoint, ClaimsPrincipal user)
    {
        var context = new DefaultHttpContext { User = user };
        context.Request.Method = method;
        context.SetEndpoint(endpoint);
        return context;
    }

    private static RouteEndpoint Endpoint(string route, params object[] metadata)
        => new(_ => Task.CompletedTask, RoutePatternFactory.Parse(route), order: 0, new EndpointMetadataCollection(metadata), displayName: route);
}
