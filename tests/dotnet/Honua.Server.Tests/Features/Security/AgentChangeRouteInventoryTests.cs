// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Infrastructure.Authentication;
using Honua.Server.Features.Admin;
using Honua.TestKit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Security;

// Every governed route the host maps that changes state (admin and Console routes, and routes
// that require administrator authority) refuses an agent, except the routes in the committed
// agent-route-rules.txt, which apply their own agent rule. A new route is refused without being
// listed; giving one its own rule fails this test until its line, with the reason, is added.
[Collection("Database")]
public sealed class AgentChangeRouteInventoryTests : IAsyncLifetime
{
    private const string AllowListFileName = "agent-route-rules.txt";
    private readonly WebAppFixture _fixture = new();

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [Fact]
    [Trait("Category", "Architecture")]
    public void GovernedChangeRoutes_WithTheirOwnAgentRule_MatchTheCommittedList()
    {
        var allowed = GovernedChangeRoutes()
            .Where(route => AgentChangeRoutes.HasRouteAgentRule(route.Endpoint))
            .Select(route => $"{route.Method} {route.Pattern} | {route.Endpoint.Metadata.GetMetadata<RouteAgentRuleMetadata>()!.Reason}")
            .Order(StringComparer.Ordinal)
            .ToArray();

        var committed = File.ReadAllLines(Path.Join(RepositoryRoot(), "tests", "dotnet", "Honua.Server.Tests", "Features", "Security", AllowListFileName))
            .Where(line => line.Trim().Length > 0 && !line.TrimStart().StartsWith('#'))
            .Select(line => line.Trim())
            .ToArray();

        committed.Should().BeInAscendingOrder(StringComparer.Ordinal).And.OnlyHaveUniqueItems();
        allowed.Should().Equal(committed,
            "{0} must list every governed change route that applies its own agent rule, with its reason. Expected content:\n{1}",
            AllowListFileName,
            string.Join('\n', allowed));
    }

    [Fact]
    [Trait("Category", "Architecture")]
    public void GovernedChangeRoutes_WithoutTheirOwnAgentRule_RefuseAnAgent()
    {
        var agent = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(AdminApiKeyPermission.PermissionClaimType, AgentPrincipal.AgentGrant)], "test"));
        var routes = GovernedChangeRoutes().Where(route => !AgentChangeRoutes.HasRouteAgentRule(route.Endpoint)).ToArray();

        routes.Should().HaveCountGreaterThan(150, "the host maps the admin and Console surface; if not, this guard means nothing");
        routes.Should().Contain(route => !route.Pattern.Contains("/admin", StringComparison.OrdinalIgnoreCase)
                && !route.Pattern.Contains("/console", StringComparison.OrdinalIgnoreCase),
            "routes that require administrator authority outside the admin and Console paths are governed too");
        var open = routes
            .Where(route =>
            {
                var context = new DefaultHttpContext { User = agent };
                context.Request.Method = route.Method == "ANY" ? HttpMethods.Post : route.Method;
                context.SetEndpoint(route.Endpoint);
                return !AgentRouteAdmissionMiddleware.IsRefused(context);
            })
            .Select(route => $"{route.Method} {route.Pattern}")
            .ToArray();

        open.Should().BeEmpty("an agent changes administrative state only through the operations API or MCP");
    }

    // Reads what each policy requires, not its name, so a policy left off the governed list fails here.
    [Fact]
    [Trait("Category", "Architecture")]
    public async Task ChangeRoutesUnderAPolicyThatRequiresAdministratorAuthority_AreGoverned()
    {
        var policies = _fixture.Services.GetRequiredService<IAuthorizationPolicyProvider>();
        RequiresAdministratorAuthority(await policies.GetPolicyAsync(AuthenticationExtensions.AdminPolicy))
            .Should().BeTrue("the check must recognise the administrator policy itself");

        var ungoverned = new List<string>();
        foreach (var endpoint in RouteEndpoints().Where(endpoint => !AgentChangeRoutes.IsGoverned(endpoint) && ChangesState(endpoint)))
        {
            foreach (var name in endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(data => data.Policy).OfType<string>())
            {
                if (RequiresAdministratorAuthority(await policies.GetPolicyAsync(name)))
                {
                    ungoverned.Add($"{name} {endpoint.RoutePattern.RawText}");
                }
            }
        }

        ungoverned.Should().BeEmpty("a change route that requires administrator authority must refuse an agent; add its policy to AgentChangeRoutes");
    }

    private static bool RequiresAdministratorAuthority(AuthorizationPolicy? policy)
        => policy is not null && policy.Requirements.Any(static requirement =>
            requirement is AdminPermissionRequirement or AdminApproveRequirement or AdminCredentialManagementRequirement or OpsReadRequirement
            || (requirement is RolesAuthorizationRequirement roles && roles.AllowedRoles.Contains("admin", StringComparer.OrdinalIgnoreCase)));

    private static bool ChangesState(RouteEndpoint endpoint)
        => endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods is not { Count: > 0 } methods
            || methods.Any(AgentChangeRoutes.IsChange);

    private IEnumerable<RouteEndpoint> RouteEndpoints()
        => _fixture.Services.GetServices<EndpointDataSource>().SelectMany(source => source.Endpoints).OfType<RouteEndpoint>();

    private IEnumerable<(string Method, string Pattern, RouteEndpoint Endpoint)> GovernedChangeRoutes()
        => RouteEndpoints()
            .Where(AgentChangeRoutes.IsGoverned)
            .SelectMany(endpoint => (endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods is { Count: > 0 } methods ? methods : ["ANY"])
                .Where(method => method == "ANY" || AgentChangeRoutes.IsChange(method))
                .Select(method => (method.ToUpperInvariant(), endpoint.RoutePattern.RawText!, endpoint)))
            .DistinctBy(route => (route.Item1, route.Item2));

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Join(directory.FullName, "Honua.sln")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull("the repository root (Honua.sln) must be reachable from the test output directory");
        return directory!.FullName;
    }
}
