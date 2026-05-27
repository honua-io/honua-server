// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Edit;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Security;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Caching;
using Honua.Server.Features.Infrastructure.Events;
using Honua.Server.Features.Infrastructure.Validation;
using Honua.Server.Features.Protocols.OData.Models;
using Honua.Server.Features.Protocols.OData.Services;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using NSubstitute;
using MetadataV2ServiceProtocols = Honua.Core.Features.Metadata.Domain.V2.ServiceProtocols;

namespace Honua.Server.Tests.Features.Protocols.OData.Services;

public sealed class ODataBatchHandlerTests
{
    [Fact]
    public async Task ProcessBatchAsync_WithAtomicCreate_ReadsPersistedFeatureForResponse()
    {
        var featureReader = Substitute.For<IFeatureReader>();
        var featureWriter = Substitute.For<IFeatureWriter>();
        var layer = CreateLayer();
        var service = CreateService(layer);
        var createdFeature = CreateFeature(101, "Persisted name");

        featureReader.GetAsync(layer.Id, 101, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Feature?>(createdFeature));
        featureWriter.ApplyEditsAsync(default, default!, default)
            .ReturnsForAnyArgs(FeatureEditResult.Success(
                createdCount: 1,
                updatedCount: 0,
                deletedCount: 0,
                createdIds: ImmutableArray.Create(101L),
                createResults: ImmutableArray.Create(EditOperationResult.Success(101))));

        var sut = CreateSut(featureReader, featureWriter);
        var request = new ODataBatchRequest
        {
            Requests =
            [
                new ODataBatchRequestItem
                {
                    Id = "create-city",
                    Method = "POST",
                    Url = $"Layers({layer.Id})/Features",
                    AtomicityGroup = "g1",
                    Body = new Dictionary<string, object?>
                    {
                        ["LayerId"] = layer.Id,
                        ["Attributes"] = new Dictionary<string, object?>
                        {
                            ["name"] = "Created in batch"
                        }
                    }
                }
            ]
        };

        var response = await sut.ProcessBatchAsync(CreateContext(layer, service, "admin"), request, "https://example.test", CancellationToken.None);

        response.Responses.Should().ContainSingle();
        var createResponse = response.Responses[0];
        createResponse.Status.Should().Be(201);
        createResponse.Headers.Should().ContainKey("ETag");

        var payload = createResponse.Body.Should().BeOfType<Dictionary<string, object?>>().Subject;
        payload["ObjectId"].Should().Be(101L);
        payload["name"].Should().Be("Persisted name");
        payload.Should().ContainKey("@odata.etag");
        payload.Should().ContainKey("@odata.context");

        await featureReader.Received(1).GetAsync(layer.Id, 101, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessBatchAsync_WithAtomicUpdate_ReadsPersistedFeatureForResponse()
    {
        var featureReader = Substitute.For<IFeatureReader>();
        var featureWriter = Substitute.For<IFeatureWriter>();
        var layer = CreateLayer();
        var service = CreateService(layer);
        var existingFeature = CreateFeature(25, "Before");
        var persistedFeature = CreateFeature(25, "Persisted after");

        featureReader.GetAsync(layer.Id, existingFeature.Id, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Feature?>(existingFeature), Task.FromResult<Feature?>(persistedFeature));
        featureWriter.ApplyEditsAsync(default, default!, default)
            .ReturnsForAnyArgs(FeatureEditResult.Success(
                createdCount: 0,
                updatedCount: 1,
                deletedCount: 0,
                updateResults: ImmutableArray.Create(EditOperationResult.Success(existingFeature.Id))));

        var sut = CreateSut(featureReader, featureWriter);
        var request = new ODataBatchRequest
        {
            Requests =
            [
                new ODataBatchRequestItem
                {
                    Id = "update-city",
                    Method = "PATCH",
                    Url = $"Features({layer.Id},{existingFeature.Id})",
                    AtomicityGroup = "g1",
                    Body = new Dictionary<string, object?>
                    {
                        ["Attributes"] = new Dictionary<string, object?>
                        {
                            ["name"] = "After"
                        }
                    }
                }
            ]
        };

        var response = await sut.ProcessBatchAsync(CreateContext(layer, service, "admin"), request, "https://example.test", CancellationToken.None);

        response.Responses.Should().ContainSingle();
        var updateResponse = response.Responses[0];
        updateResponse.Status.Should().Be(200);
        updateResponse.Headers.Should().ContainKey("ETag");

        var payload = updateResponse.Body.Should().BeOfType<Dictionary<string, object?>>().Subject;
        payload["ObjectId"].Should().Be(25L);
        payload["name"].Should().Be("Persisted after");
        payload.Should().ContainKey("@odata.etag");
        payload.Should().ContainKey("@odata.context");

        await featureReader.Received(2).GetAsync(layer.Id, existingFeature.Id, Arg.Any<CancellationToken>());
    }

    private static ODataBatchHandler CreateSut(
        IFeatureReader featureReader,
        IFeatureWriter featureWriter)
    {
        var dependencies = new ODataBatchDependencies(
            featureReader,
            featureWriter,
            Substitute.For<IGeometryService>(),
            new FeatureMutationValidator(Substitute.For<IGeometryValidator>()),
            Substitute.For<ICrsRegistry>(),
            new EditLimits(),
            new ODataValidationService(Substitute.For<ICommonQueryValidator>()),
            new ETagService(),
            new ODataEditParameterAdapter(Substitute.For<ILogger<ODataEditParameterAdapter>>()),
            new EditProcessor(Substitute.For<ILogger<EditProcessor>>()),
            new NoOpFeatureChangeEventPublisher());

        return new ODataBatchHandler(
            dependencies,
            dependencies.ETagService,
            Substitute.For<ILogger>());
    }

    private static DefaultHttpContext CreateContext(LayerDefinition layer, ServiceDefinition service, params string[] roles)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAccessPolicyEvaluator, AccessPolicyEvaluator>();
        services.AddSingleton<IOptions<RbacOptions>>(Options.Create(new RbacOptions()));
        services.AddSingleton<IMetadataV2GraphProvider>(CreateMetadataProvider(layer, service));

        var context = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider()
        };

        if (roles.Length > 0)
        {
            var claims = roles.Select(role => new Claim(ClaimTypes.Role, role)).ToArray();
            context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
        }

        return context;
    }

    private sealed class NoOpFeatureChangeEventPublisher : IFeatureChangeEventPublisher
    {
        public Task PublishAsync(FeatureChangeEventRequest request, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task PublishStrictAsync(FeatureChangeEventRequest request, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private static LayerDefinition CreateLayer()
        => new(
            1,
            "cities",
            null,
            GeometryType.None,
            SpatialReference.WGS84,
            [
                new FieldDefinition(FieldNames.ObjectId, FieldType.Integer, Nullable: false),
                new FieldDefinition("name", FieldType.String, Length: 128)
            ]);

    private static ServiceDefinition CreateService(LayerDefinition layer)
        => new(
            "cities",
            "cities service",
            [layer],
            SpatialReference.WGS84);

    private static TestMetadataV2GraphProvider CreateMetadataProvider(LayerDefinition layer, ServiceDefinition service)
    {
        var resourceId = $"res-layer-{layer.Id.ToString(CultureInfo.InvariantCulture)}";
        var bindingId = $"binding-layer-{layer.Id.ToString(CultureInfo.InvariantCulture)}";
        var serviceId = $"svc-{service.Name}";

        return new TestMetadataV2GraphBuilder()
            .AddResource(
                resourceId,
                layer.Name,
                MetadataV2ResourceType.FeatureDataset,
                fields: layer.Fields.Select(MapField))
            .AddStorageBinding(
                bindingId,
                resourceId,
                $"test.layers.{layer.Id.ToString(CultureInfo.InvariantCulture)}",
                storageLayerId: layer.Id)
            .AddService(
                serviceId,
                service.Name,
                protocols: ["OData"])
            .AddPublication(
                $"{serviceId}-layer-{layer.Id.ToString(CultureInfo.InvariantCulture)}",
                serviceId,
                resourceId,
                layerIndex: layer.Id,
                storageBindingId: bindingId,
                publicationType: MetadataV2PublicationType.ODataEntitySet)
            .BuildProvider();
    }

    private static MetadataV2Field MapField(FieldDefinition field)
        => new()
        {
            Name = field.Name,
            Type = field.Type switch
            {
                FieldType.Integer => MetadataV2FieldType.Integer,
                FieldType.BigInteger => MetadataV2FieldType.BigInteger,
                FieldType.Double => MetadataV2FieldType.Double,
                FieldType.Date => MetadataV2FieldType.DateTime,
                FieldType.Boolean => MetadataV2FieldType.Boolean,
                _ => MetadataV2FieldType.String
            },
            Nullable = field.Nullable,
            Length = field.Length
        };

    private static Feature CreateFeature(long id, string name)
        => Feature.Create(
            id,
            geometry: null,
            ImmutableDictionary<string, object?>.Empty.Add("name", name));
}
