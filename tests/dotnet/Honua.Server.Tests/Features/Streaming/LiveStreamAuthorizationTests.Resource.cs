// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.MultiTenancy;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Streaming;

public sealed partial class LiveStreamAuthorizationTests
{
    // The resource check runs in a fresh scope, so it must see the tenant and route the stream's own request resolved.
    [UnitTest]
    public async Task Revalidate_ResourceCheck_SeesTheStreamTenantAndRouteAndCanRefuse()
    {
        await using var services = CreateServicesWithTenantContext();
        using var scope = services.CreateScope();
        var token = await IssueAsync(services.GetRequiredService<IPortalTokenIssuer>(), "tenant-a", ["reader"]);
        var context = Context(scope.ServiceProvider, token.Token);
        context.User = (await context.AuthenticateAsync(PortalTokenAuthenticationExtensions.PortalTokenScheme)).Principal!;
        scope.ServiceProvider.GetRequiredService<RequestTenantContext>().Set("tenant-a", TenantContextSource.Header);
        context.Request.RouteValues["mapId"] = "map-1";
        var resource = new RecordingResourceAuthorization { Allow = true };
        context.SetEndpoint(new Endpoint(null, new EndpointMetadataCollection(resource), "stream"));

        (await LiveStreamAuthorizationFilter.RevalidateAsync(context, PortalTokenAuthenticationExtensions.PortalTokenScheme, CancellationToken.None))
            .Should().BeTrue();
        resource.SeenTenant.Should().Be(("tenant-a", TenantContextSource.Header));
        resource.SeenMapId.Should().Be("map-1");

        resource.Allow = false;
        (await LiveStreamAuthorizationFilter.RevalidateAsync(context, PortalTokenAuthenticationExtensions.PortalTokenScheme, CancellationToken.None))
            .Should().BeFalse("a refused resource check ends the stream even though the credential is still valid");
    }

    private static ServiceProvider CreateServicesWithTenantContext()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMemoryCache();
        services.AddSingleton<Honua.Infrastructure.Security.ISecurityTokenStore, Honua.Infrastructure.Security.InMemorySecurityTokenStore>();
        services.AddSingleton<IPortalTokenIssuer, PortalTokenIssuer>();
        services.AddScoped<RequestTenantContext>();
        services.AddScoped<ITenantContext>(provider => provider.GetRequiredService<RequestTenantContext>());
        services.AddAuthentication(PortalTokenAuthenticationExtensions.PortalTokenScheme)
            .AddScheme<AuthenticationSchemeOptions, PortalTokenAuthenticationHandler>(PortalTokenAuthenticationExtensions.PortalTokenScheme, _ => { });
        return services.BuildServiceProvider();
    }

    private sealed class RecordingResourceAuthorization : ILiveStreamResourceAuthorization
    {
        public bool Allow { get; set; }

        public (string? TenantId, TenantContextSource Source) SeenTenant { get; private set; }

        public string? SeenMapId { get; private set; }

        public Task<bool> IsAuthorizedAsync(HttpContext check, CancellationToken cancellationToken)
        {
            var tenant = check.RequestServices.GetRequiredService<ITenantContext>();
            SeenTenant = (tenant.TenantId, tenant.Source);
            SeenMapId = check.Request.RouteValues["mapId"] as string;
            return Task.FromResult(Allow);
        }
    }
}
