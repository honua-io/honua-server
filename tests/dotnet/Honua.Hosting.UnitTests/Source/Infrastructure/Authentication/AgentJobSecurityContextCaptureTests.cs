// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Infrastructure.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Honua.Hosting.UnitTests.Infrastructure.Authentication;

public sealed class AgentJobSecurityContextCaptureTests
{
    [Fact]
    public async Task CurrentCapture_UsesActualStoredCredentialAndRequestTenantWithoutOpeningGenericCapture()
    {
        var keys = new InMemoryAdminApiKeyStore();
        var created = await keys.CreateAsync("capture-worker", ["agent:workspace"], null, null, default);
        var principal = ApiKeyAuthenticationHandler.CreateStoredKeyPrincipal(created.Record);
        ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim("access_token", "fixture-secret-never-persisted"));
        var tenant = Substitute.For<ITenantContext>();
        tenant.TenantId.Returns("request-tenant");
        using var services = new ServiceCollection().AddSingleton<IAdminApiKeyStore>(keys)
            .AddSingleton<IOptions<RbacOptions>>(Options.Create(new RbacOptions())).AddSingleton(tenant).BuildServiceProvider();
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = principal, RequestServices = services } };
        var capture = new AgentJobSecurityContextCapture(accessor);
        var context = await capture.CaptureCurrentAsync();
        Assert.Equal("request-tenant", context.TenantId);
        Assert.Equal(created.Record.Id.ToString(), context.AgentCredential!.Id);
        Assert.Equal(StoredApiKeyCredentialVersion.FromRecord(created.Record), context.AgentCredential.CredentialVersion);
        Assert.DoesNotContain(context.Claims, claim => claim.Type == "access_token");
        Assert.Throws<BadHttpRequestException>(() => JobSecurityContextCapture.Capture(principal, new RbacOptions(), tenant));
        Assert.Throws<UnauthorizedAccessException>(() => JobSecurityContextCapture.Restore(context));
        await keys.RotateAsync(created.Record.Id, default);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => capture.CaptureCurrentAsync());
    }

    [Theory]
    [InlineData("no-request")]
    [InlineData("human")]
    [InlineData("forged-provenance")]
    public async Task CurrentCapture_RejectsMissingAgentRequestOrUntrustedCredentialProvenance(string kind)
    {
        var keys = new InMemoryAdminApiKeyStore();
        var created = await keys.CreateAsync("capture-worker", ["agent:workspace"], null, null, default);
        var actual = ApiKeyAuthenticationHandler.CreateStoredKeyPrincipal(created.Record);
        var principal = kind == "human"
            ? new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "human")], "Human"))
            : new ClaimsPrincipal(new ClaimsIdentity(actual.Claims.Select(claim => new Claim(claim.Type, claim.Value)), AuthenticationExtensions.ApiKeyScheme));
        using var services = new ServiceCollection().AddSingleton<IAdminApiKeyStore>(keys)
            .AddSingleton<IOptions<RbacOptions>>(Options.Create(new RbacOptions())).BuildServiceProvider();
        var accessor = new HttpContextAccessor
        {
            HttpContext = kind == "no-request" ? null : new DefaultHttpContext { User = principal, RequestServices = services }
        };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => new AgentJobSecurityContextCapture(accessor).CaptureCurrentAsync());
    }
}
