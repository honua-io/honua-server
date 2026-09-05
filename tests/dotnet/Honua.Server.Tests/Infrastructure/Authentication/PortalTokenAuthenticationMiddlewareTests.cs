// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Infrastructure.Authentication;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Infrastructure.Authentication;

public sealed class PortalTokenAuthenticationMiddlewareTests
{
    [UnitTest]
    public async Task InvokeAsync_OAuthFormToken_DoesNotAttemptPortalAuthentication()
    {
        var nextCalled = false;
        var options = Substitute.For<IOptionsMonitor<PortalTokenAuthenticationOptions>>();
        options.CurrentValue.Returns(new PortalTokenAuthenticationOptions { Enabled = true });
        var middleware = new PortalTokenAuthenticationMiddleware(
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            options);
        var context = new DefaultHttpContext();
        context.Request.Path = "/sharing/rest/oauth2/revoke";
        context.Request.ContentType = "application/x-www-form-urlencoded";

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeTrue();
        context.User.Identity?.IsAuthenticated.Should().BeFalse();
    }
}
