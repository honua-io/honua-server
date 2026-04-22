// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using FluentAssertions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Security;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Shared.Models;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Validation;
using Honua.Server.Features.OData;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using CatalogGeometryType = Honua.Core.Features.Catalog.Domain.GeometryType;

namespace Honua.Server.Tests.Features.OData;

/// <summary>
/// Unit tests for OData layer visibility gating.
/// </summary>
[Protocol(Protocols.ODataV4)]
public sealed class ODataQueryHandlerTests
{
    [UnitTest]
    public void IsODataLayerVisible_WithNonODataPrimaryService_UsesLayerMetadataAccess()
    {
        var layer = CreateLayer(1, odataEnabled: true, allowAnonymous: true);
        var service = CreateService("alpha", layer, odataEnabled: false, allowAnonymous: false);
        var primaryServices = LayerValidationHelpers.BuildPrimaryServiceMap([service], ServiceProtocols.OData);

        var visible = InvokeIsODataLayerVisible(layer, primaryServices);

        visible.Should().BeTrue();
    }

    private static bool InvokeIsODataLayerVisible(
        LayerDefinition layer,
        IReadOnlyDictionary<int, ServiceDefinition> primaryServices)
    {
        var method = typeof(ODataQueryHandler).GetMethod(
            "IsODataLayerVisible",
            BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull();

        using var provider = new ServiceCollection()
            .AddSingleton<IAccessPolicyEvaluator, AccessPolicyEvaluator>()
            .BuildServiceProvider();

        var context = new DefaultHttpContext
        {
            RequestServices = provider
        };

        return (bool)method!.Invoke(null, [context, layer, primaryServices])!;
    }

    private static LayerDefinition CreateLayer(
        int id,
        bool odataEnabled = false,
        bool allowAnonymous = false)
    {
        var metadata = odataEnabled || allowAnonymous
            ? new CatalogMetadata
            {
                EnabledProtocols = odataEnabled ? [ServiceProtocols.OData] : null,
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
        bool odataEnabled = true,
        bool allowAnonymous = false)
    {
        string[]? enabledProtocols =
            odataEnabled
                ? [ServiceProtocols.OData]
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
