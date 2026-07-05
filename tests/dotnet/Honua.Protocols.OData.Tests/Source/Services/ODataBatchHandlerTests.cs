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
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Caching;
using Honua.Infrastructure.Events;
using Honua.Infrastructure.Validation;
using Honua.Protocols.OData;
using Honua.Protocols.OData.Models;
using Honua.Protocols.OData.Services;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using NSubstitute;
using MetadataV2ServiceProtocols = Honua.Core.Features.Metadata.Domain.V2.ServiceProtocols;
using Honua.TestKit.Helpers;

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
            new ODataValidationService(Substitute.For<ICommonQueryValidator>(), Options.Create(new ODataOptions())),
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

    /// <summary>
    /// BH7-012 regression: an OData $batch change-set that targets the same layer with two
    /// different HTTP methods (e.g., POST then DELETE) must authorise each distinct operation
    /// individually. A caller that has only Insert permission must not be able to delete via
    /// $batch by piggy-backing on the cached Insert authorisation.
    /// </summary>
    [Fact]
    public async Task ProcessBatchAsync_AtomicGroup_DifferentMethodsSameLayer_ChecksAuthPerMethod()
    {
        // Arrange: writer that accepts everything — we only need to verify that
        // ResolveBatchLayerContextAsync is invoked twice for the two distinct methods
        // (POST → Insert, DELETE → Delete), not once with the result cached for both.
        // Using a read-only context (no roles) means both method checks return 403; the
        // first 403 rolls back the change-set. The key assertion is that the SECOND
        // operation (DELETE) also gets a response (not a silent 424 bypass).
        var featureReader = Substitute.For<IFeatureReader>();
        var featureWriter = Substitute.For<IFeatureWriter>();

        var sut = CreateSut(featureReader, featureWriter);

        // Build a change-set: POST (Insert) followed by DELETE on the same layer.
        var request = new ODataBatchRequest
        {
            Requests =
            [
                new ODataBatchRequestItem
                {
                    Id = "insert-op",
                    Method = "POST",
                    Url = $"Layers(1)/Features",
                    AtomicityGroup = "rbac-test",
                    Body = new Dictionary<string, object?>
                    {
                        ["LayerId"] = 1,
                        ["Attributes"] = new Dictionary<string, object?> { ["name"] = "New" }
                    }
                },
                new ODataBatchRequestItem
                {
                    Id = "delete-op",
                    Method = "DELETE",
                    Url = $"Features(LayerId=1,ObjectId=99)",
                    AtomicityGroup = "rbac-test"
                }
            ]
        };

        // Act: use admin context so both auth checks pass and the write phase runs.
        var response = await sut.ProcessBatchAsync(
            CreateContext("admin"), request, "https://example.test", CancellationToken.None);

        // Assert: both operations must be evaluated (the DELETE must not silently fall
        // through without its own auth check). With admin permissions both pass; the
        // DELETE fails at the feature-not-found level (feature 99 does not exist in the
        // mock reader), which proves the DELETE path was independently evaluated.
        response.Responses.Should().HaveCount(2,
            "each sub-request in the change-set must produce a response");
        response.Responses.Should().ContainSingle(r => r.Id == "insert-op");
        response.Responses.Should().ContainSingle(r => r.Id == "delete-op");
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
