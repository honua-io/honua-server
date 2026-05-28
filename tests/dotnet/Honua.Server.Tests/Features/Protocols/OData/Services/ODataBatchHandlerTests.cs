// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using FluentAssertions;
using Honua.Core.Configuration;
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
        var createdFeature = CreateFeature(101, "Persisted name");

        featureReader.GetAsync(1, 101, Arg.Any<CancellationToken>())
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
                    Url = $"Layers({1})/Features",
                    AtomicityGroup = "g1",
                    Body = new Dictionary<string, object?>
                    {
                        ["LayerId"] = 1,
                        ["Attributes"] = new Dictionary<string, object?>
                        {
                            ["name"] = "Created in batch"
                        }
                    }
                }
            ]
        };

        var response = await sut.ProcessBatchAsync(CreateContext("admin"), request, "https://example.test", CancellationToken.None);

        response.Responses.Should().ContainSingle();
        var createResponse = response.Responses[0];
        createResponse.Status.Should().Be(201);
        createResponse.Headers.Should().ContainKey("ETag");

        var payload = createResponse.Body.Should().BeOfType<Dictionary<string, object?>>().Subject;
        payload["ObjectId"].Should().Be(101L);
        payload["name"].Should().Be("Persisted name");
        payload.Should().ContainKey("@odata.etag");
        payload.Should().ContainKey("@odata.context");

        await featureReader.Received(1).GetAsync(1, 101, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessBatchAsync_WithAtomicUpdate_ReadsPersistedFeatureForResponse()
    {
        var featureReader = Substitute.For<IFeatureReader>();
        var featureWriter = Substitute.For<IFeatureWriter>();
        var existingFeature = CreateFeature(25, "Before");
        var persistedFeature = CreateFeature(25, "Persisted after");

        featureReader.GetAsync(1, existingFeature.Id, Arg.Any<CancellationToken>())
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
                    Url = $"Features({1},{existingFeature.Id})",
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

        var response = await sut.ProcessBatchAsync(CreateContext("admin"), request, "https://example.test", CancellationToken.None);

        response.Responses.Should().ContainSingle();
        var updateResponse = response.Responses[0];
        updateResponse.Status.Should().Be(200);
        updateResponse.Headers.Should().ContainKey("ETag");

        var payload = updateResponse.Body.Should().BeOfType<Dictionary<string, object?>>().Subject;
        payload["ObjectId"].Should().Be(25L);
        payload["name"].Should().Be("Persisted after");
        payload.Should().ContainKey("@odata.etag");
        payload.Should().ContainKey("@odata.context");

        await featureReader.Received(2).GetAsync(1, existingFeature.Id, Arg.Any<CancellationToken>());
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

    private static DefaultHttpContext CreateContext(params string[] roles)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAccessPolicyEvaluator, AccessPolicyEvaluator>();
        services.AddSingleton<IOptions<RbacOptions>>(Options.Create(new RbacOptions()));
        services.AddSingleton<IMetadataV2GraphProvider>(CreateMetadataProvider());

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

    private static TestMetadataV2GraphProvider CreateMetadataProvider()
        => new TestMetadataV2GraphBuilder()
            .AddResource(
                "res-layer-1",
                "cities",
                MetadataV2ResourceType.FeatureDataset,
                fields:
                [
                    new MetadataV2Field { Name = FieldNames.ObjectId, Type = MetadataV2FieldType.Integer, Nullable = false },
                    new MetadataV2Field { Name = "name", Type = MetadataV2FieldType.String, Length = 128 }
                ])
            .AddStorageBinding("binding-layer-1", "res-layer-1", "test.layers.1", storageLayerId: 1)
            .AddService("svc-cities", "cities", protocols: ["OData"])
            .AddPublication(
                "svc-cities-layer-1",
                "svc-cities",
                "res-layer-1",
                layerIndex: 1,
                storageBindingId: "binding-layer-1",
                publicationType: MetadataV2PublicationType.ODataEntitySet)
            .BuildProvider();

    private static Feature CreateFeature(long id, string name)
        => Feature.Create(
            id,
            geometry: null,
            ImmutableDictionary<string, object?>.Empty.Add("name", name));
}
