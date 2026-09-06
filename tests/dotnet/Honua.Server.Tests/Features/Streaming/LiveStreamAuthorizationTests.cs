// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Security;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using System.Security.Claims;

namespace Honua.Server.Tests.Features.Streaming;

public sealed class LiveStreamAuthorizationTests
{
    [UnitTest]
    public async Task Filter_EndpointDenialCancelsPendingCheck_DoesNotStartAnSseResponse()
    {
        var authentication = Substitute.For<IAuthenticationService>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        authentication.AuthenticateAsync(Arg.Any<HttpContext>(), Arg.Any<string>()).Returns(async call =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, call.ArgAt<HttpContext>(0).RequestAborted);
            return AuthenticateResult.NoResult();
        });
        var registrations = new ServiceCollection();
        registrations.AddLogging();
        registrations.AddAuthentication().AddScheme<AuthenticationSchemeOptions, PortalTokenAuthenticationHandler>(
            PortalTokenAuthenticationExtensions.PortalTokenScheme, _ => { });
        registrations.AddSingleton(authentication);
        await using var services = registrations.BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "denied")],
            PortalTokenAuthenticationExtensions.PortalTokenScheme));
        using var output = new MemoryStream();
        context.Response.Body = output;
        var result = await new LiveStreamAuthorizationFilter().InvokeAsync(EndpointFilterInvocationContext.Create(context), async _ =>
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        });
        ((IStatusCodeHttpResult)result!).StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        output.Length.Should().Be(0, "normal cancellation must not overwrite a typed denial with a terminal stream frame");
    }

    [UnitTest]
    public async Task Revalidate_RevokedRealToken_DoesNotReuseCachedRequestAuthentication()
    {
        await using var services = CreateServices();
        using var scope = services.CreateScope();
        var issuer = services.GetRequiredService<IPortalTokenIssuer>();
        var token = await IssueAsync(issuer, "tenant-a", ["reader"]);
        var context = Context(scope.ServiceProvider, token.Token);
        var initial = await context.AuthenticateAsync(PortalTokenAuthenticationExtensions.PortalTokenScheme);
        initial.Succeeded.Should().BeTrue();
        context.User = initial.Principal!;
        CanonicalSecurityActor.StampRequestBinding(context.User, "tenant-a");
        (await LiveStreamAuthorizationFilter.RevalidateAsync(context, PortalTokenAuthenticationExtensions.PortalTokenScheme, CancellationToken.None))
            .Should().BeTrue();

        var admitted = false;
        context.Features.Set<IAuthenticateResultFeature>(new PolicyAuthenticationFeature
        {
            AuthenticateResult = AuthenticateResult.Success(new AuthenticationTicket(context.User, "context.User"))
        });
        await new LiveStreamAuthorizationFilter().InvokeAsync(
            EndpointFilterInvocationContext.Create(context), _ =>
            {
                admitted = true;
                return ValueTask.FromResult<object?>(Results.Empty);
            });
        admitted.Should().BeTrue("a framework-normalized scheme must resolve to its registered authentication handler");
        var identity = (ClaimsIdentity)context.User.Identity!;
        foreach (var claim in identity.FindAll(CanonicalSecurityActor.AuthenticationSchemeClaim).ToArray()) { identity.RemoveClaim(claim); }
        admitted = false;
        await new LiveStreamAuthorizationFilter().InvokeAsync(EndpointFilterInvocationContext.Create(context), _ =>
        {
            admitted = true;
            return ValueTask.FromResult<object?>(Results.Empty);
        });
        admitted.Should().BeTrue("a synthetic authorization ticket must not hide an otherwise registered handler on a tenantless principal");

        await issuer.RevokeAsync(token.Token, CancellationToken.None);
        (await context.AuthenticateAsync(PortalTokenAuthenticationExtensions.PortalTokenScheme)).Succeeded
            .Should().BeTrue("ASP.NET caches authentication on the original request; this is the regression trigger");
        (await LiveStreamAuthorizationFilter.RevalidateAsync(context, PortalTokenAuthenticationExtensions.PortalTokenScheme, CancellationToken.None))
            .Should().BeFalse("the fresh handler must observe production token revocation");
    }

    [Theory]
    [InlineData("tenant-b", "reader")]
    [InlineData("tenant-a", "admin")]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    public async Task Revalidate_ReplacementChangesTenantOrRoles_RejectsCapturedScope(string tenant, string role)
    {
        await using var services = CreateServices();
        using var scope = services.CreateScope();
        var issuer = services.GetRequiredService<IPortalTokenIssuer>();
        var original = await IssueAsync(issuer, "tenant-a", ["reader"]);
        var replacement = await IssueAsync(issuer, tenant, [role]);
        var context = Context(scope.ServiceProvider, original.Token);
        var initial = await context.AuthenticateAsync(PortalTokenAuthenticationExtensions.PortalTokenScheme);
        initial.Succeeded.Should().BeTrue();
        context.User = initial.Principal!;
        context.Request.QueryString = new QueryString("?token=" + replacement.Token);
        (await LiveStreamAuthorizationFilter.RevalidateAsync(context, PortalTokenAuthenticationExtensions.PortalTokenScheme, CancellationToken.None))
            .Should().BeFalse("a stream's tenant and permission scope must remain bound to its admitted identity");
    }

    private static ServiceProvider CreateServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMemoryCache();
        services.AddSingleton<IPortalTokenIssuer, PortalTokenIssuer>();
        services.AddAuthentication(PortalTokenAuthenticationExtensions.PortalTokenScheme)
            .AddScheme<AuthenticationSchemeOptions, PortalTokenAuthenticationHandler>(PortalTokenAuthenticationExtensions.PortalTokenScheme, _ => { });
        return services.BuildServiceProvider();
    }

    private sealed class PolicyAuthenticationFeature : IAuthenticateResultFeature
    {
        public AuthenticateResult? AuthenticateResult { get; set; }
    }

    private static Task<PortalTokenIssuance> IssueAsync(IPortalTokenIssuer issuer, string tenant, string[] roles) =>
        issuer.IssueAsync(new PortalTokenIssueRequest("subject-a", "Subject A", tenant, roles,
            PortalTokenClientType.Referer, "https://stream-proof.example/", DateTimeOffset.UtcNow.AddMinutes(1)), CancellationToken.None);

    private static DefaultHttpContext Context(IServiceProvider services, string token)
    {
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.QueryString = new QueryString("?token=" + token);
        context.Request.Headers.Referer = "https://stream-proof.example/";
        return context;
    }
}
