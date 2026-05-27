// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections;
using System.Reflection;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.Edit;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Events.Outbox;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Query;
using Honua.Core.Features.Security;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Core.Queries.Filters;
using Honua.Server.Features.Infrastructure.Events;
using Honua.Server.Features.Infrastructure.Validation;
using Honua.Server.Features.Protocols.Ogc.Api.Features.Services;
using Honua.Server.Features.Protocols.Ogc.Classic.Wfs20;
using Honua.Server.Features.Protocols.Ogc.Classic.Wfs20.Services;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using MetadataV2ServiceProtocols = Honua.Core.Features.Metadata.Domain.V2.ServiceProtocols;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wfs20;

/// <summary>
/// Unit tests for WFS 2.0 publication gating.
/// </summary>
[Protocol(TestProtocols.Wfs20)]
public sealed class Wfs20EnablementTests
{
    [UnitTest]
    public async Task GetPublishedFeatureTypesAsync_WithWfsPublication_ReturnsMetadataV2Descriptor()
    {
        var provider = CreateProvider(serviceProtocols: [MetadataV2ServiceProtocols.Wfs20]);
        var handler = CreateHandler(provider);
        using var serviceProvider = CreateServiceProvider();
        var context = new DefaultHttpContext { RequestServices = serviceProvider };

        var descriptors = await InvokeGetPublishedFeatureTypesAsync(handler, context);

        descriptors.Should().ContainSingle();
        GetDescriptorProperty<int>(descriptors[0], "StorageLayerId").Should().Be(1);
        GetDescriptorProperty<string>(descriptors[0], "LocalName").Should().Be("layer1");
        GetDescriptorProperty<MetadataV2Resource>(descriptors[0], "Resource").Metadata.Name.Should().Be("Layer1");
        GetDescriptorProperty<MetadataV2Service>(descriptors[0], "Service").Metadata.Name.Should().Be("alpha");
    }

    [UnitTest]
    public async Task GetPublishedFeatureTypesAsync_WithoutWfsProtocol_SkipsPublication()
    {
        var provider = CreateProvider(serviceProtocols: [MetadataV2ServiceProtocols.OgcFeatures]);
        var handler = CreateHandler(provider);
        using var serviceProvider = CreateServiceProvider();
        var context = new DefaultHttpContext { RequestServices = serviceProvider };

        var descriptors = await InvokeGetPublishedFeatureTypesAsync(handler, context);

        descriptors.Should().BeEmpty();
    }

    private static async Task<object[]> InvokeGetPublishedFeatureTypesAsync(
        Wfs20Handler handler,
        HttpContext context)
    {
        var method = typeof(Wfs20Handler).GetMethod(
            "GetPublishedFeatureTypesAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);
        method.Should().NotBeNull();

        var task = (Task)method!.Invoke(handler, [context, CancellationToken.None])!;
        await task.ConfigureAwait(false);
        var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
        return ((IEnumerable)result).Cast<object>().ToArray();
    }

    private static T GetDescriptorProperty<T>(object descriptor, string propertyName)
    {
        var value = descriptor.GetType().GetProperty(propertyName)!.GetValue(descriptor);
        value.Should().BeAssignableTo<T>();
        return (T)value!;
    }

    private static ServiceProvider CreateServiceProvider()
    {
        return new ServiceCollection()
            .AddSingleton<IAccessPolicyEvaluator, AccessPolicyEvaluator>()
            .BuildServiceProvider();
    }

    private static TestMetadataV2GraphProvider CreateProvider(IReadOnlyList<string> serviceProtocols)
    {
        const int storageLayerId = 1;
        const string resourceId = "res-layer-1";
        const string bindingId = "binding-layer-1";
        const string serviceId = "svc-alpha";

        return new TestMetadataV2GraphBuilder()
            .AddResource(
                resourceId,
                "Layer1",
                accessPolicy: new AccessPolicy { AllowAnonymous = true })
            .AddStorageBinding(
                bindingId,
                resourceId,
                "test.layers.1",
                storageLayerId: storageLayerId)
            .AddService(
                serviceId,
                "alpha",
                protocols: serviceProtocols,
                accessPolicy: new AccessPolicy { AllowAnonymous = true })
            .AddPublication(
                $"{serviceId}-layer-1",
                serviceId,
                resourceId,
                layerIndex: storageLayerId,
                storageBindingId: bindingId,
                publicationType: MetadataV2PublicationType.OgcCollection)
            .BuildProvider();
    }

    private static Wfs20Handler CreateHandler(IMetadataV2GraphProvider metadataProvider)
    {
        var coordinateTransformService = Substitute.For<ICoordinateTransformService>();
        var queryServices = new Wfs20QueryServices(
            Substitute.For<IFeatureReader>(),
            Substitute.For<IFeatureWriter>(),
            Substitute.For<IGmlFeatureStore>(),
            metadataProvider,
            Substitute.For<IFilterExpressionService>(),
            new Wfs20QueryParameterAdapter(NullLogger<Wfs20QueryParameterAdapter>.Instance),
            Substitute.For<IQueryProcessor>(),
            new Wfs20EditParameterAdapter(NullLogger<Wfs20EditParameterAdapter>.Instance),
            Substitute.For<IEditProcessor>(),
            new OgcFeaturesGeometryServices(
                Substitute.For<IGeometryService>(),
                coordinateTransformService,
                Options.Create(new LimitsOptions()),
                NullLogger<OgcFeaturesGeometryServices>.Instance),
            new FeatureMutationValidator(Substitute.For<IGeometryValidator>()),
            new FeatureMutationEventService(
                Substitute.For<IFeatureChangeEventPublisher>(),
                outboxCapabilityProvider: Substitute.For<IOutboxCapabilityProvider>()),
            coordinateTransformService,
            Substitute.For<ICrsRegistry>(),
            Options.Create(new Wfs20Options()),
            Options.Create(new LimitsOptions()));

        return new Wfs20Handler(NullLogger<Wfs20Handler>.Instance, queryServices);
    }
}
