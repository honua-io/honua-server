// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Encodings.Web;
using System.Text.Json;
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
using StackExchange.Redis;

namespace Honua.Server.Tests.Features.OperationsToolset;

[Trait("Tier", "Fast")]
public sealed class ApprovedReplayTenantAuthenticationTests
{
    [Fact]
    public async Task ApprovedCredential_RotationCannotReissueSealedAuthority()
    {
        var store = new InMemoryAdminApiKeyStore();
        var key = await store.CreateAsync("approved-operation:proposal-a",
            [AdminApiKeyPermission.CreateApprovedOperationGrant("PUT", "/api/v1/admin/metadata/layers/1/filter"),
                "admin:operation:tenant:tenant-a"], DateTimeOffset.UtcNow.AddMinutes(5), "requester", CancellationToken.None);

        (await store.RotateAsync(key.Record.Id, CancellationToken.None)).Should().BeNull();
        (await store.ValidateAsync(key.Key, CancellationToken.None)).Should().NotBeNull();
    }

    [Fact]
    public async Task RedisApprovedCredential_RotationCannotReissueSealedAuthority()
    {
        var issuer = new InMemoryAdminApiKeyStore();
        var key = await issuer.CreateAsync("approved-operation:proposal-a",
            [AdminApiKeyPermission.CreateApprovedOperationGrant("PUT", "/api/v1/admin/metadata/layers/1/filter"),
                "admin:operation:tenant:tenant-a"], DateTimeOffset.UtcNow.AddMinutes(5), "requester", CancellationToken.None);
        var database = Substitute.For<IDatabase>();
        database.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns((RedisValue)JsonSerializer.Serialize(key.Record));
        var redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);
        var store = new RedisAdminApiKeyStore(redis);

        (await store.RotateAsync(key.Record.Id, CancellationToken.None)).Should().BeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApprovedCredential_MissingOrConflictingTenantBinding_FailsAuthentication(bool conflicting)
    {
        var store = new InMemoryAdminApiKeyStore();
        var grants = new List<string>
        {
            AdminApiKeyPermission.CreateApprovedOperationGrant("PUT", "/api/v1/admin/metadata/layers/1/filter"),
        };
        if (conflicting)
        {
            grants.Add("admin:operation:tenant:tenant-a");
            grants.Add("admin:operation:tenant:tenant-b");
        }

        var key = await store.CreateAsync("approved-operation:proposal-a", grants,
            DateTimeOffset.UtcNow.AddMinutes(5), "requester", CancellationToken.None);
        var context = new DefaultHttpContext();
        context.Request.Headers["X-API-Key"] = key.Key;
        var options = Substitute.For<IOptionsMonitor<AuthenticationSchemeOptions>>();
        options.Get(Arg.Any<string>()).Returns(new AuthenticationSchemeOptions());
        var handler = new ApiKeyAuthenticationHandler(options, NullLoggerFactory.Instance, UrlEncoder.Default,
            new ApiKeyAuthenticationDependencies(Options.Create(new ApiKeyAuthenticationOptions()), adminApiKeyStore: store));
        await handler.InitializeAsync(new AuthenticationScheme("ApiKey", null, typeof(ApiKeyAuthenticationHandler)), context);

        (await handler.AuthenticateAsync()).Succeeded.Should().BeFalse();
    }

    [Theory]
    [InlineData("tenant-a", true, "tenant-a")]
    [InlineData("tenant-b", true, "tenant-a")]
    [InlineData(null, true, "tenant-a")]
    [InlineData("tenant-b", false, "tenant-a")]
    [InlineData(null, false, "")]
    public async Task ApprovedCredential_AuthenticationAndTenantResolution_UsePersistedTenant(string? header, bool tenantResolutionEnabled, string sealedTenant)
    {
        var store = new InMemoryAdminApiKeyStore();
        var key = await store.CreateAsync("approved-operation:proposal-a",
            [AdminApiKeyPermission.CreateApprovedOperationGrant("PUT", "/api/v1/admin/metadata/layers/1/filter"),
                "admin:operation:tenant:" + sealedTenant], DateTimeOffset.UtcNow.AddMinutes(5), "requester", CancellationToken.None);
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
        var transformation = new OidcClaimsTransformation(
            Options.Create(new OidcAuthenticationOptions()),
            NullLogger<OidcClaimsTransformation>.Instance,
            services);
        context.User = await transformation.TransformAsync(authentication.Principal!);
        context.User = await transformation.TransformAsync(context.User);
        var invoked = false;
        var middleware = new TenantContextMiddleware(_ =>
        {
            invoked = true;
            return Task.CompletedTask;
        }, Options.Create(new TenantContextOptions { Enabled = tenantResolutionEnabled }), NullLogger<TenantContextMiddleware>.Instance);
        await middleware.InvokeAsync(context);

        invoked.Should().BeTrue();
        tenant.TenantId.Should().Be(string.IsNullOrEmpty(sealedTenant) ? null : sealedTenant);
        context.User.IsInRole("platform_admin").Should().BeFalse();
        context.User.IsInRole("multi_tenant_admin").Should().BeFalse();
        AdminApiKeyPermission.IsAuthorized(context.User, "PUT", "/api/v1/admin/metadata/layers/1/filter").Should().BeTrue();
        AdminApiKeyPermission.IsAuthorized(context.User, "PUT", "/api/v1/admin/metadata/layers/2/filter").Should().BeFalse();
        AdminApiKeyPermission.IsAuthorized(context.User, "DELETE", "/api/v1/admin/metadata/layers/1/filter").Should().BeFalse();
    }
}
