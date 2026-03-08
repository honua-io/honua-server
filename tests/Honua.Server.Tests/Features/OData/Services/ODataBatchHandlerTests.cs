// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Server.Features.Infrastructure.Caching;
using Honua.Server.Features.Infrastructure.Events;
using Honua.Server.Features.Infrastructure.Validation;
using Honua.Server.Features.OData.Models;
using Honua.Server.Features.OData.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Honua.Server.Tests.Features.OData.Services;

public sealed class ODataBatchHandlerTests
{
    [Fact]
    public async Task ProcessBatchAsync_WithAtomicCreate_ReadsPersistedFeatureForResponse()
    {
        var layerCatalog = Substitute.For<ILayerCatalog>();
        var featureReader = Substitute.For<IFeatureReader>();
        var featureWriter = Substitute.For<IFeatureWriter>();
        var layer = CreateLayer();
        var createdFeature = CreateFeature(101, "Persisted name");

        layerCatalog.GetLayerAsync(layer.Id, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<LayerDefinition?>(layer));
        featureReader.GetAsync(layer.Id, 101, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Feature?>(createdFeature));
        featureWriter.ApplyEditsAsync(default, default!, default)
            .ReturnsForAnyArgs(FeatureEditResult.Success(
                createdCount: 1,
                updatedCount: 0,
                deletedCount: 0,
                createdIds: ImmutableArray.Create(101L),
                createResults: ImmutableArray.Create(EditOperationResult.Success(101))));

        var sut = CreateSut(layerCatalog, featureReader, featureWriter);
        var request = new ODataBatchRequest
        {
            Requests =
            [
                new ODataBatchRequestItem
                {
                    Id = "create-city",
                    Method = "POST",
                    Url = "/odata/Features",
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

        var response = await sut.ProcessBatchAsync(request, "https://example.test", CancellationToken.None);

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
        var layerCatalog = Substitute.For<ILayerCatalog>();
        var featureReader = Substitute.For<IFeatureReader>();
        var featureWriter = Substitute.For<IFeatureWriter>();
        var layer = CreateLayer();
        var existingFeature = CreateFeature(25, "Before");
        var persistedFeature = CreateFeature(25, "Persisted after");

        layerCatalog.GetLayerAsync(layer.Id, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<LayerDefinition?>(layer));
        featureReader.GetAsync(layer.Id, existingFeature.Id, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Feature?>(existingFeature), Task.FromResult<Feature?>(persistedFeature));
        featureWriter.ApplyEditsAsync(default, default!, default)
            .ReturnsForAnyArgs(FeatureEditResult.Success(
                createdCount: 0,
                updatedCount: 1,
                deletedCount: 0,
                updateResults: ImmutableArray.Create(EditOperationResult.Success(existingFeature.Id))));

        var sut = CreateSut(layerCatalog, featureReader, featureWriter);
        var request = new ODataBatchRequest
        {
            Requests =
            [
                new ODataBatchRequestItem
                {
                    Id = "update-city",
                    Method = "PATCH",
                    Url = $"/odata/Features({layer.Id},{existingFeature.Id})",
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

        var response = await sut.ProcessBatchAsync(request, "https://example.test", CancellationToken.None);

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
        ILayerCatalog layerCatalog,
        IFeatureReader featureReader,
        IFeatureWriter featureWriter)
    {
        var dependencies = new ODataBatchDependencies(
            layerCatalog,
            featureReader,
            featureWriter,
            Substitute.For<IGeometryService>(),
            new FeatureMutationValidator(Substitute.For<IGeometryValidator>()),
            Substitute.For<ICrsRegistry>(),
            new EditLimits(),
            new ODataValidationService(Substitute.For<ICommonQueryValidator>()),
            new ETagService(),
            new NoOpFeatureChangeEventPublisher());

        return new ODataBatchHandler(
            dependencies,
            dependencies.ETagService,
            Substitute.For<ILogger>());
    }

    private sealed class NoOpFeatureChangeEventPublisher : IFeatureChangeEventPublisher
    {
        public Task PublishAsync(FeatureChangeEventRequest request, CancellationToken cancellationToken = default)
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

    private static Feature CreateFeature(long id, string name)
        => Feature.Create(
            id,
            geometry: null,
            ImmutableDictionary<string, object?>.Empty.Add("name", name));
}
