// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Server.Features.Protocols.OData.Services;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Protocols.OData;

/// <summary>
/// Unit tests for OData metadata-v2 publication visibility.
/// </summary>
[Protocol(TestProtocols.ODataV4)]
public sealed class ODataQueryHandlerTests
{
    [UnitTest]
    public async Task ResolveVisibleODataPublications_WithNonODataService_DoesNotUseResourceProtocolFallback()
    {
        using var provider = CreateProvider(["FeatureServer"]);
        var context = CreateContext(provider);

        var visible = await ODataV2Lookups.ResolveVisibleODataPublicationsAsync(context, CancellationToken.None);

        visible.Should().BeEmpty();
    }

    [UnitTest]
    public async Task ResolveVisibleODataPublications_WithODataService_ReturnsPublication()
    {
        using var provider = CreateProvider(["OData"]);
        var context = CreateContext(provider);

        var visible = await ODataV2Lookups.ResolveVisibleODataPublicationsAsync(context, CancellationToken.None);

        visible.Should().ContainSingle();
        visible[0].LayerIndex.Should().Be(1);
        visible[0].StorageLayerId.Should().Be(101);
        visible[0].Resource.Metadata.Name.Should().Be("Layer1");
    }

    private static ServiceProvider CreateProvider(IReadOnlyList<string> protocols)
    {
        var graphProvider = new TestMetadataV2GraphBuilder()
            .AddResource(
                "res-layer-1",
                "Layer1",
                fields:
                [
                    new MetadataV2Field
                    {
                        Name = "objectid",
                        Type = MetadataV2FieldType.Integer,
                        Nullable = false
                    },
                    new MetadataV2Field
                    {
                        Name = "name",
                        Type = MetadataV2FieldType.String
                    }
                ],
                accessPolicy: new AccessPolicy { AllowAnonymous = true })
            .AddStorageBinding(
                "binding-layer-1",
                "res-layer-1",
                "test.layers.1",
                storageLayerId: 101)
            .AddService(
                "svc-alpha",
                "alpha",
                protocols: protocols,
                accessPolicy: new AccessPolicy { AllowAnonymous = true })
            .AddPublication(
                "pub-alpha-layer-1",
                "svc-alpha",
                "res-layer-1",
                layerIndex: 1,
                storageBindingId: "binding-layer-1",
                publicationType: MetadataV2PublicationType.ODataEntitySet)
            .BuildProvider();

        return new ServiceCollection()
            .AddSingleton<IAccessPolicyEvaluator, AccessPolicyEvaluator>()
            .AddSingleton<IMetadataV2GraphProvider>(graphProvider)
            .BuildServiceProvider();
    }

    private static DefaultHttpContext CreateContext(ServiceProvider provider)
        => new DefaultHttpContext
        {
            RequestServices = provider
        };
}
