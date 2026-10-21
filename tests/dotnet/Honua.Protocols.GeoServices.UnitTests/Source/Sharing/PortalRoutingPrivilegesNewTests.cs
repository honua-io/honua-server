// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Security.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Protocols.GeoServices.Sharing;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.Sharing;

/// <summary>Portal routing discovery reflects the existing execution authorization.</summary>
public sealed class PortalRoutingPrivilegesNewTests
{
    [UnitTest]
    public async Task PortalAdmin_AdvertisesOnlyAuthorizedRoutingAndTheActualOrganization()
    {
        var (context, helpers) = Context(admin: true, allowed: true, scopeAllowed: true);
        using var services = (ServiceProvider)context.RequestServices;
        var user = await SharingRestEndpoints.BuildPortalUserAsync(context, helpers, NullLogger<SharingRestLog>.Instance);

        user.Should().NotBeNull();
        user!.Role.Should().Be("org_admin");
        user.OrgId.Should().Be("0123456789ABCDEF");
        user.Privileges.Should().Equal("premium:user:networkanalysis");
    }

    [UnitTest]
    public async Task PortalUser_DoesNotAdvertiseDeniedOrUnconfiguredRouting()
    {
        foreach (var denied in new[] { "grant", "scope", "helper" })
        {
            var (context, helpers) = Context(admin: false, allowed: denied != "grant", scopeAllowed: denied != "scope");
            using var services = (ServiceProvider)context.RequestServices;
            var user = await SharingRestEndpoints.BuildPortalUserAsync(context,
                denied == "helper" ? new PortalHelperServices() : helpers, NullLogger<SharingRestLog>.Instance);

            user!.Role.Should().Be("org_user");
            user.Privileges.Should().BeEmpty();
        }
    }

    [UnitTest]
    public async Task AnonymousPortal_DoesNotAcquireAUserOrRoutingPrivileges()
    {
        var (context, helpers) = Context(admin: true, allowed: true, scopeAllowed: true);
        using var services = (ServiceProvider)context.RequestServices;
        context.User = new ClaimsPrincipal(new ClaimsIdentity());
        (await SharingRestEndpoints.BuildPortalUserAsync(context, helpers, NullLogger<SharingRestLog>.Instance))
            .Should().BeNull();
    }

    private static (HttpContext, PortalHelperServices) Context(bool admin, bool allowed, bool scopeAllowed)
    {
        var authorization = Substitute.For<IOperatorAuthorizationEvaluator>();
        authorization.EvaluateAsync(Arg.Any<ClaimsPrincipal>(), Arg.Is<OperatorAuthorizationRequest>(request =>
                request.ResourceType == OperatorResourceType.Process && request.Operation == OperatorOperation.Execute),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(allowed ? AccessDecision.Allowed() : AccessDecision.Forbidden()));
        var scopes = Substitute.For<IOperatorScopeAuthorizer>();
        scopes.Evaluate(Arg.Any<ClaimsPrincipal>(), OperatorResourceType.Process, OperatorOperation.Execute)
            .Returns(scopeAllowed ? OperatorScopeDecision.Allowed() : OperatorScopeDecision.Denied("Missing process scope"));
        var services = new ServiceCollection()
            .AddSingleton(authorization)
            .AddSingleton(scopes)
            .AddSingleton(Options.Create(new RbacOptions()))
            .BuildServiceProvider();
        var identity = new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, "subject"), new Claim(ClaimTypes.Name, "member"),
            new Claim("roles", admin ? "admin" : "reader"),
        ], "PortalToken");
        return (new DefaultHttpContext { RequestServices = services, User = new ClaimsPrincipal(identity) },
            new PortalHelperServices { AsyncRoute = new PortalHelperService { Url = "https://gis.example/arcgis/rest/services/RoutingRoute/GPServer" } });
    }
}
