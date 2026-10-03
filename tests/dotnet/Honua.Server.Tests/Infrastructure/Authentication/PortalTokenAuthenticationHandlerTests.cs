// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Infrastructure.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Infrastructure.Authentication;

public sealed class PortalTokenAuthenticationHandlerTests
{
    private const string Referer = "https://native-proof.example/";

    [Theory]
    [InlineData("/rest/services/protected/ImageServer", false, 1)]
    [InlineData("/services/protected/ImageServer", false, 1)]
    [InlineData("/sharing/rest/community/groups", true, 1)]
    [InlineData("/rest/services/protected/ImageServer", false, 2)]
    [InlineData("/services/protected/ImageServer", false, 2)]
    [InlineData("/sharing/rest/community/groups", true, 2)]
    [InlineData("/rest/services/protected/ImageServer", false, 3)]
    [InlineData("/services/protected/ImageServer", false, 3)]
    [InlineData("/sharing/rest/community/groups", true, 3)]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    public async Task AuthenticateAsync_IdenticalRepeatedToken_AuthenticatesBoundPrincipalAndPreservesBody(
        string path, bool form, int copies)
    {
        await using var services = CreateServices();
        var issued = await IssueAsync(services);
        var context = Context(services, path, form, Enumerable.Repeat(issued.Token, copies));
        var originalBody = ((MemoryStream)context.Request.Body).ToArray();

        var result = await context.AuthenticateAsync(PortalTokenAuthenticationExtensions.PortalTokenScheme);

        result.Succeeded.Should().BeTrue();
        result.Principal!.Identity!.Name.Should().Be("native-reader");
        context.Request.Body.Position.Should().Be(0);
        ((MemoryStream)context.Request.Body).ToArray().Should().Equal(originalBody);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    public async Task AuthenticateAsync_ConflictingRepeatedToken_RejectsBothOrdersAndDoesNotFallBack(
        bool form, bool empty, bool reverse)
    {
        await using var services = CreateServices();
        var issued = await IssueAsync(services);
        var values = new[] { issued.Token, empty ? "" : "another-token" };
        if (reverse)
        {
            Array.Reverse(values);
        }

        var context = Context(services, "/services/protected/ImageServer", form, values);
        if (!form)
        {
            context.Request.Headers.Authorization = "Bearer " + issued.Token;
        }

        var result = await context.AuthenticateAsync(PortalTokenAuthenticationExtensions.PortalTokenScheme);

        result.Succeeded.Should().BeFalse();
        result.Failure.Should().NotBeNull();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    public async Task AuthenticateAsync_RepeatedRevokedOrWrongBindingToken_StillRejects(bool form, bool revoked)
    {
        await using var services = CreateServices();
        var issued = await IssueAsync(services);
        var context = Context(services, "/services/protected/ImageServer", form, [issued.Token, issued.Token]);
        if (revoked)
        {
            await services.GetRequiredService<IPortalTokenIssuer>().RevokeAsync(issued.Token, CancellationToken.None);
        }
        else
        {
            context.Request.Headers.Referer = "https://other.example/";
        }

        var result = await context.AuthenticateAsync(PortalTokenAuthenticationExtensions.PortalTokenScheme);

        result.Succeeded.Should().BeFalse();
        result.Failure.Should().NotBeNull();
    }

    private static ServiceProvider CreateServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMemoryCache();
        services.AddSingleton<IPortalTokenIssuer, PortalTokenIssuer>();
        services.AddAuthentication(PortalTokenAuthenticationExtensions.PortalTokenScheme)
            .AddScheme<AuthenticationSchemeOptions, PortalTokenAuthenticationHandler>(
                PortalTokenAuthenticationExtensions.PortalTokenScheme, _ => { });
        return services.BuildServiceProvider();
    }

    private static Task<PortalTokenIssuance> IssueAsync(ServiceProvider services) =>
        services.GetRequiredService<IPortalTokenIssuer>().IssueAsync(new PortalTokenIssueRequest(
            "native-reader", "Native Reader", null, ["reader"], PortalTokenClientType.Referer,
            Referer, DateTimeOffset.UtcNow.AddMinutes(2)), CancellationToken.None);

    private static DefaultHttpContext Context(
        IServiceProvider services, string path, bool form, IEnumerable<string> tokens)
    {
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Path = path;
        context.Request.Method = "POST";
        context.Request.Headers.Referer = Referer;
        var encoded = string.Join('&', tokens.Select(token => "token=" + Uri.EscapeDataString(token)));
        if (form)
        {
            context.Request.ContentType = "application/x-www-form-urlencoded";
            context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(encoded + "&title=preserved"));
        }
        else
        {
            context.Request.QueryString = new QueryString("?" + encoded);
            context.Request.ContentType = "text/xml";
            context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("<GetServiceInfo/>"));
        }

        return context;
    }
}
