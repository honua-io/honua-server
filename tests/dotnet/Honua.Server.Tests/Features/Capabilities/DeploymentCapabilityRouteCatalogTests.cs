// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Features.Capabilities;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;

namespace Honua.Server.Tests.Features.Capabilities;

[global::Xunit.Trait("Tier", "Fast")]
public sealed class DeploymentCapabilityRouteCatalogTests
{
    [UnitTest]
    public void Resolve_VersionedAliasRequestFirst_DoesNotPoisonEndpointCache()
    {
        var catalog = new DeploymentCapabilityRouteCatalog();
        var endpoint = new RouteEndpoint(
            _ => Task.CompletedTask,
            RoutePatternFactory.Parse("/api/v{version:apiVersion}/admin/version"),
            order: 0,
            EndpointMetadataCollection.Empty,
            "AdminVersion");

        var aliasContext = CreateGetContext("/api/v1.0/admin/version", endpoint);
        var canonicalContext = CreateGetContext("/api/v1/admin/version", endpoint);

        var aliasResolution = catalog.Resolve(aliasContext);
        var canonicalResolution = catalog.Resolve(canonicalContext);

        aliasResolution.IsRouted.Should().BeTrue();
        aliasResolution.Capability.Should().NotBeNullOrWhiteSpace();
        canonicalResolution.Should().Be(aliasResolution);
    }

    private static DefaultHttpContext CreateGetContext(string path, Endpoint endpoint)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = path;
        context.SetEndpoint(endpoint);
        return context;
    }
}
