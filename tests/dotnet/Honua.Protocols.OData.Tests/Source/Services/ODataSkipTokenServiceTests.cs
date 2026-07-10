// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Protocols.OData.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Honua.Protocols.OData.Tests.Services;

public sealed class ODataSkipTokenServiceTests
{
    [Fact]
    public void ResolveSkipTokenDiscriminator_TenantAndAuthenticatedUser_ReturnsStableScopedValue()
    {
        var tenantContext = Substitute.For<ITenantContext>();
        tenantContext.TenantId.Returns("tenant-a");
        using var services = new ServiceCollection()
            .AddSingleton(tenantContext)
            .BuildServiceProvider();
        var context = new DefaultHttpContext
        {
            RequestServices = services,
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "operator-1")],
                authenticationType: "test"))
        };

        var discriminator = ODataRequestValidation.ResolveSkipTokenDiscriminator(context);

        discriminator.Should().Be("tenant:tenant-a|subject:user:operator-1");
    }

    [Fact]
    public void TryDecode_SameQueryDifferentRequestDiscriminator_ReturnsFalse()
    {
        var token = ODataSkipTokenService.Encode(
            offset: 25,
            filter: "ObjectId gt 0",
            orderby: "ObjectId asc",
            requestDiscriminator: "tenant:alpha|subject:user:one");

        var decoded = ODataSkipTokenService.TryDecode(
            token,
            filter: "ObjectId gt 0",
            orderby: "ObjectId asc",
            requestDiscriminator: "tenant:alpha|subject:user:two",
            out _,
            out var error);

        decoded.Should().BeFalse();
        error.Should().Contain("tenant");
    }

    [Fact]
    public void TryDecode_SameQuerySameRequestDiscriminator_ReturnsOffset()
    {
        var token = ODataSkipTokenService.Encode(
            offset: 25,
            filter: "ObjectId gt 0",
            orderby: "ObjectId asc",
            requestDiscriminator: "tenant:alpha|subject:user:one");

        var decoded = ODataSkipTokenService.TryDecode(
            token,
            filter: "ObjectId gt 0",
            orderby: "ObjectId asc",
            requestDiscriminator: "tenant:alpha|subject:user:one",
            out var offset,
            out var error);

        decoded.Should().BeTrue(error);
        offset.Should().Be(25);
    }
}
