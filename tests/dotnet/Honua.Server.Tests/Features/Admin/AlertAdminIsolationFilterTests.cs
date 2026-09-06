// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Infrastructure.MultiTenancy;
using Honua.Server.Features.Admin;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Server.Tests.Features.Admin;

public sealed class AlertAdminIsolationFilterTests
{
    [Theory]
    [Trait("Tier", "Fast")]
    [InlineData("tenant-a", true, "tenant_id")]
    [InlineData("tenant-b", true, "tenant_id")]
    [InlineData("", true, "tenant_id")]
    [InlineData("tenant-a", false, "tenant_id")]
    [InlineData("tenant-a", false, "TENANT_ID")]
    [InlineData("tenant-b", false, "TiD")]
    public async Task InvokeAsync_TenantClaim_NeverCallsInstanceStore(string tenantId, bool resolutionEnabled, string claimType)
    {
        using var services = new ServiceCollection()
            .Configure<TenantContextOptions>(options => options.Enabled = resolutionEnabled)
            .BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "tenant-administrator"), new Claim(claimType, tenantId), new Claim(ClaimTypes.Role, "admin")], "Test"));
        var called = false;
        var result = await new AlertAdminIsolationFilter().InvokeAsync(
            new DefaultEndpointFilterInvocationContext(context), _ => { called = true; return ValueTask.FromResult<object?>(null); });
        called.Should().BeFalse();
        result.Should().BeAssignableTo<IStatusCodeHttpResult>().Which.StatusCode.Should().Be(403);
    }

    [Theory]
    [Trait("Tier", "Fast")]
    [InlineData(TenantContextSource.Claim)]
    [InlineData(TenantContextSource.Header)]
    public async Task InvokeAsync_ResolvedTenant_NeverCallsInstanceStore(TenantContextSource source)
    {
        var tenant = Substitute.For<ITenantContext>();
        tenant.Source.Returns(source);
        tenant.TenantId.Returns("tenant-a");
        using var services = new ServiceCollection().AddOptions()
            .Configure<TenantContextOptions>(_ => { }).AddSingleton(tenant).BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        var called = false;
        var result = await new AlertAdminIsolationFilter().InvokeAsync(
            new DefaultEndpointFilterInvocationContext(context), _ => { called = true; return ValueTask.FromResult<object?>(null); });
        called.Should().BeFalse();
        result.Should().BeAssignableTo<IStatusCodeHttpResult>().Which.StatusCode.Should().Be(403);
    }
}
