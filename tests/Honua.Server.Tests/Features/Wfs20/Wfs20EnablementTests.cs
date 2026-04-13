// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using FluentAssertions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Security;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Shared.Models;
using Honua.Server.Features.Wfs20.Services;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using CatalogGeometryType = Honua.Core.Features.Catalog.Domain.GeometryType;

namespace Honua.Server.Tests.Features.Wfs20;

/// <summary>
/// Unit tests for WFS 2.0 publication gating.
/// </summary>
[Protocol(Protocols.Wfs20)]
public sealed class Wfs20EnablementTests
{
    [UnitTest]
    public void IsPublishedForWfs_WithServiceEnabledAndLayerDisabled_ReturnsTrue()
    {
        var layer = CreateLayer(1, wfsEnabled: false, allowAnonymous: true);
        var service = CreateService("alpha", layer, wfsEnabled: true, allowAnonymous: true);

        var published = InvokeIsPublishedForWfs(layer, [service]);

        published.Should().BeTrue();
    }

    private static bool InvokeIsPublishedForWfs(LayerDefinition layer, ServiceDefinition[] services)
    {
        var method = typeof(Wfs20Handler).GetMethod(
            "IsPublishedForWfs",
            BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull();

        using var provider = new ServiceCollection()
            .AddSingleton<IAccessPolicyEvaluator, AccessPolicyEvaluator>()
            .BuildServiceProvider();

        var context = new DefaultHttpContext
        {
            RequestServices = provider
        };

        return (bool)method!.Invoke(null, [context, layer, services])!;
    }

    private static LayerDefinition CreateLayer(
        int id,
        bool wfsEnabled = false,
        bool allowAnonymous = false)
    {
        string[]? enabledProtocols =
            wfsEnabled
                ? [ServiceProtocols.Wfs20]
                : [ServiceProtocols.FeatureServer];
        var metadata = wfsEnabled || allowAnonymous
            ? new CatalogMetadata
            {
                EnabledProtocols = enabledProtocols,
                AccessPolicy = allowAnonymous
                    ? new AccessPolicy { AllowAnonymous = true }
                    : null
            }
            : null;

        return new LayerDefinition(
            id,
            $"Layer{id}",
            "Test layer",
            CatalogGeometryType.Point,
            SpatialReference.WGS84,
            [
                new FieldDefinition("objectid", FieldType.Integer, Nullable: false),
                new FieldDefinition("name", FieldType.String, Length: 128)
            ],
            Metadata: metadata);
    }

    private static ServiceDefinition CreateService(
        string name,
        LayerDefinition layer,
        bool wfsEnabled = true,
        bool allowAnonymous = false)
    {
        string[]? enabledProtocols =
            wfsEnabled
                ? [ServiceProtocols.Wfs20]
                : [ServiceProtocols.FeatureServer];
        var metadata = new CatalogMetadata
        {
            EnabledProtocols = enabledProtocols,
            AccessPolicy = allowAnonymous
                ? new AccessPolicy { AllowAnonymous = true }
                : null
        };

        return new ServiceDefinition(
            name,
            $"{name} service",
            [layer],
            SpatialReference.WGS84,
            Metadata: metadata);
    }
}
