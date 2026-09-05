// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Encodings.Web;
using FluentAssertions;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Middleware;
using Honua.Infrastructure.MultiTenancy;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Operations;

[Trait("Tier", "Fast")]
public sealed class ApprovedReplayTenantAuthenticationTests
{
    [Theory]
    [InlineData("tenant-a")]
    [InlineData("tenant-b")]
    [InlineData(null)]
    public async Task ApprovedCredential_AuthenticationAndTenantResolution_UsePersistedTenant(string? header)
    {
        var store = new InMemoryAdminApiKeyStore();
        var key = await store.CreateAsync("approved-operation:proposal-a",
            [AdminApiKeyPermission.CreateApprovedOperationGrant("PUT", "/api/v1/admin/metadata/layers/1/filter"),
                "admin:operation:tenant:tenant-a"], DateTimeOffset.UtcNow.AddMinutes(5), "requester", CancellationToken.None);
        var tenant = new RequestTenantContext();
        using var services = new ServiceCollection().AddSingleton<ITenantContext>(tenant).BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Method = "PUT";
        context.Request.Path = "/api/v1/admin/metadata/layers/1/filter";
        context.Request.Headers["X-API-Key"] = key.Key;
        if (header is not null)
        {
            context.Request.Headers[TenantContextOptions.TenantHeaderName] = header;
        }

        var options = Substitute.For<IOptionsMonitor<AuthenticationSchemeOptions>>();
        options.Get(Arg.Any<string>()).Returns(new AuthenticationSchemeOptions());
        var handler = new ApiKeyAuthenticationHandler(options, NullLoggerFactory.Instance, UrlEncoder.Default,
            new ApiKeyAuthenticationDependencies(Options.Create(new ApiKeyAuthenticationOptions()), adminApiKeyStore: store));
        await handler.InitializeAsync(new AuthenticationScheme("ApiKey", null, typeof(ApiKeyAuthenticationHandler)), context);
        var authentication = await handler.AuthenticateAsync();
        authentication.Succeeded.Should().BeTrue();
        context.User = authentication.Principal!;
        var invoked = false;
        var middleware = new TenantContextMiddleware(_ =>
        {
            invoked = true;
            return Task.CompletedTask;
        }, Options.Create(new TenantContextOptions()), NullLogger<TenantContextMiddleware>.Instance);
        await middleware.InvokeAsync(context);

        invoked.Should().BeTrue();
        tenant.TenantId.Should().Be("tenant-a");
        context.User.IsInRole("platform_admin").Should().BeFalse();
        context.User.IsInRole("multi_tenant_admin").Should().BeFalse();
        AdminApiKeyPermission.IsAuthorized(context.User, "PUT", "/api/v1/admin/metadata/layers/1/filter").Should().BeTrue();
        AdminApiKeyPermission.IsAuthorized(context.User, "PUT", "/api/v1/admin/metadata/layers/2/filter").Should().BeFalse();
        AdminApiKeyPermission.IsAuthorized(context.User, "DELETE", "/api/v1/admin/metadata/layers/1/filter").Should().BeFalse();
    }
}
