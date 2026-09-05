// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Infrastructure.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Server.Tests.Infrastructure.Middleware;

[Trait("Tier", "Fast")]
public sealed class AdminAuditTenantTests
{
    [Theory]
    [InlineData("tenant-a", 200)]
    [InlineData("tenant-b", 200)]
    [InlineData("tenant-a", 400)]
    [InlineData("tenant-a", 403)]
    [InlineData(null, 401)]
    [InlineData(null, 200)]
    public async Task AdminMutation_RecordsEffectiveTenantAlongsideActorAndOutcome(string? effectiveTenant, int status)
    {
        var tenant = Substitute.For<ITenantContext>();
        tenant.TenantId.Returns(effectiveTenant);
        var auditLog = Substitute.For<IAuditLog>();
        AuditEvent? captured = null;
        auditLog.RecordAsync(Arg.Do<AuditEvent>(value => captured = value), Arg.Any<CancellationToken>()).Returns("audit-a");
        using var services = new ServiceCollection().AddSingleton(tenant).AddSingleton(auditLog).BuildServiceProvider();
        var context = new DefaultHttpContext
        {
            RequestServices = services,
            User = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim(ClaimTypes.NameIdentifier, "same-platform-actor"),
                new Claim(ClaimTypes.Role, "platform_admin")], "Test"))
        };
        context.Request.Method = "PUT";
        context.Request.Path = "/api/v1/admin/metadata/layers/1/filter";
        context.Request.Headers["X-Honua-Tenant"] = "untrusted-header-tenant";
        context.Request.Headers["X-Correlation-ID"] = "correlation-a";
        context.SetEndpoint(new RouteEndpoint(_ => Task.CompletedTask,
            RoutePatternFactory.Parse("/api/v{version:apiVersion}/admin/metadata/layers/{layerId}/filter"), 0, EndpointMetadataCollection.Empty, "filter"));
        var middleware = new AuditLogMiddleware(_ =>
        {
            context.Response.StatusCode = status;
            return Task.CompletedTask;
        }, new DefaultAuditActionResolver());
        await middleware.InvokeAsync(context);

        captured.Should().NotBeNull();
        using var details = JsonDocument.Parse(captured!.Details);
        details.RootElement.TryGetProperty("tenantId", out var recordedTenant).Should().BeTrue();
        recordedTenant.GetString().Should().Be(effectiveTenant);
        captured.Details.Should().NotContain("untrusted-header-tenant");
        captured.Actor.Should().Be("same-platform-actor");
        details.RootElement.GetProperty("status").GetInt32().Should().Be(status);
    }
}
