// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Infrastructure.Middleware;

namespace Honua.Server.Tests.Infrastructure.Middleware;

/// <summary>
/// Unit tests for <see cref="DefaultAuditActionResolver"/> — the data-driven
/// audit coverage matrix that keeps audit emission middleware-driven (#507).
/// </summary>
public sealed class DefaultAuditActionResolverTests
{
    private readonly DefaultAuditActionResolver _resolver = new();

    [Theory]
    [InlineData("POST", "/api/v{version:apiVersion}/admin/connections")]
    [InlineData("PUT", "/api/v{version:apiVersion}/admin/connections/{id}")]
    [InlineData("PATCH", "/api/v{version:apiVersion}/admin/users/{id}")]
    [InlineData("DELETE", "/api/v{version:apiVersion}/admin/roles/{id}")]
    public void Resolve_AdminMutation_ReturnsAdminActionDescriptor(string method, string route)
    {
        var descriptor = _resolver.Resolve(method, route);

        descriptor.Should().NotBeNull();
        descriptor!.EventType.Should().Be(AuditEventType.AdminAction);
        descriptor.ResourceType.Should().Be("admin");
        descriptor.Action.Should().Be($"admin.{method.ToLowerInvariant()}");
    }

    [Theory]
    [InlineData("GET", "/api/v{version:apiVersion}/admin/connections/{id}")]
    [InlineData("HEAD", "/api/v{version:apiVersion}/admin/config")]
    [InlineData("OPTIONS", "/api/v{version:apiVersion}/admin/config")]
    public void Resolve_AdminRead_ReturnsNull(string method, string route)
    {
        _resolver.Resolve(method, route).Should().BeNull();
    }

    [Fact]
    public void Resolve_AdminLoginTokenExchange_ReturnsAuthenticationDescriptor()
    {
        var descriptor = _resolver.Resolve(
            "POST",
            "/api/v{version:apiVersion}/admin/auth/providers/{providerKey}/token");

        descriptor.Should().NotBeNull();
        descriptor!.EventType.Should().Be(AuditEventType.Authentication);
        descriptor.Action.Should().Be("auth.login");
        descriptor.ResourceType.Should().Be("session");
    }

    [Theory]
    [InlineData("/oauth/token")]
    [InlineData("/sharing/rest/oauth2/token")]
    public void Resolve_OAuthTokenIssuance_ReturnsAuthenticationDescriptor(string route)
    {
        var descriptor = _resolver.Resolve("POST", route);

        descriptor.Should().NotBeNull();
        descriptor!.EventType.Should().Be(AuditEventType.Authentication);
        descriptor.Action.Should().Be("auth.token.issue");
    }

    [Theory]
    [InlineData("GET", "/rest/services/{serviceId}/FeatureServer/{layerId:int}/query")]
    [InlineData("POST", "/ogc/features/collections/{collectionId}/items")]
    [InlineData("GET", "/healthz/live")]
    [InlineData("GET", "/odata/Layers")]
    public void Resolve_NonAuditedRoute_ReturnsNull(string method, string route)
    {
        _resolver.Resolve(method, route).Should().BeNull();
    }

    [Theory]
    [InlineData("POST", "/rest/services/{serviceId}/FeatureServer/{layerId:int}/deleteFeatures")]
    [InlineData("POST", "/rest/services/{serviceId}/FeatureServer/{layerId:int}/applyEdits")]
    [InlineData("DELETE", "/ogc/features/collections/{collectionId}/items/{featureId}")]
    public void Resolve_DestructiveFeatureWrite_ReturnsNull_EmittedByWriterDecorator(string method, string route)
    {
        // Destructive feature writes are intentionally NOT resolved by the route
        // resolver; they are emitted at the shared IFeatureWriter boundary so that
        // single-endpoint protocols (WFS-T, gRPC) are covered uniformly.
        _resolver.Resolve(method, route).Should().BeNull();
    }

    [Theory]
    [InlineData("", "/api/v1/admin/config")]
    [InlineData("POST", null)]
    [InlineData("POST", "")]
    public void Resolve_MissingInputs_ReturnsNull(string? method, string? route)
    {
        _resolver.Resolve(method!, route).Should().BeNull();
    }

    [Fact]
    public void Resolve_AdminRoute_IsCaseInsensitiveOnRouteConstraints()
    {
        // apiVersion constraint casing must not affect prefix matching.
        var descriptor = _resolver.Resolve("DELETE", "/api/v{version:APIVERSION}/admin/scenes/{id}");

        descriptor.Should().NotBeNull();
        descriptor!.EventType.Should().Be(AuditEventType.AdminAction);
    }
}
