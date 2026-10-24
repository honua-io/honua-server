// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;
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
using Honua.TestKit.Attributes;
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

        var response = await sut.ProcessBatchAsync(
            CreateContext(featureReader, featureWriter, "admin"),
            request,
            "https://example.test",
            CancellationToken.None);

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

        var response = await sut.ProcessBatchAsync(
            CreateContext(featureReader, featureWriter, "admin"),
            request,
            "https://example.test",
            CancellationToken.None);

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

    private const string NonOwnerDenial =
        "Feature is owned by another principal and cannot be modified under the owner-based edit policy.";

    private static readonly MetadataV2OwnerEditPolicy OwnerPolicy = new()
    {
        Enabled = true,
        OwnerField = "owner",
        StampOwnerOnInsert = true
    };

    private static readonly RbacOptions EditorRoles = new() { DataEditorRoles = ["editor"] };

    [UnitTest]
    public async Task ProcessBatchAsync_NonOwnerUpdateAndDelete_AreDeniedBeforeTheWrite()
    {
        var (reader, writer, applyCount, queryCount, _) = CreateTrackedStores();
        var sut = CreateSut(reader, writer);
        var request = new ODataBatchRequest
        {
            Requests =
            [
                UpdateRequest("update-city"),
                new ODataBatchRequestItem
                {
                    Id = "delete-city",
                    Method = "DELETE",
                    Url = "Features(1,42)",
                    AtomicityGroup = "g1"
                }
            ]
        };

        var response = await sut.ProcessBatchAsync(
            EditorContext(reader, writer, OwnerPolicy, "bob"),
            request,
            "https://example.test",
            CancellationToken.None);

        response.Responses.Should().HaveCount(2);
        response.Responses.Should().OnlyContain(item => item.Status == StatusCodes.Status403Forbidden);
        foreach (var item in response.Responses)
        {
            var error = item.Body.Should().BeOfType<ODataError>().Subject;
            error.Error.Code.Should().Be("Forbidden");
            error.Error.Message.Should().Be(NonOwnerDenial);
            error.Error.Message.Should().NotContain("alice");
        }

        applyCount.Value.Should().Be(0);
        queryCount.Value.Should().Be(1);
    }

    [UnitTest]
    public async Task ProcessBatchAsync_OwnerUpdate_SucceedsAndPreservesTheStoredOwner()
    {
        var (reader, writer, applyCount, queryCount, written) = CreateTrackedStores();
        var sut = CreateSut(reader, writer);

        var response = await sut.ProcessBatchAsync(
            EditorContext(reader, writer, OwnerPolicy, "alice"),
            new ODataBatchRequest { Requests = [UpdateRequest("update-city")] },
            "https://example.test",
            CancellationToken.None);

        response.Responses.Should().ContainSingle();
        response.Responses[0].Status.Should().Be(200);
        applyCount.Value.Should().Be(1);
        queryCount.Value.Should().Be(1);
        written.Value.Updates.Should().ContainSingle();
        written.Value.Updates[0].Attributes["owner"].Should().Be("alice");
        written.Value.Updates[0].Attributes["name"].Should().Be("After");
    }

    [UnitTest]
    public async Task ProcessBatchAsync_AdminUpdate_BypassesWithoutReadingTheOwner()
    {
        var (reader, writer, applyCount, queryCount, written) = CreateTrackedStores();
        var sut = CreateSut(reader, writer);

        var response = await sut.ProcessBatchAsync(
            CreateContext(reader, writer, OwnerPolicy, "carol", rbac: null, "admin"),
            new ODataBatchRequest { Requests = [UpdateRequest("update-city")] },
            "https://example.test",
            CancellationToken.None);

        response.Responses.Should().ContainSingle();
        response.Responses[0].Status.Should().Be(200);
        applyCount.Value.Should().Be(1);
        queryCount.Value.Should().Be(0);
        written.Value.Updates[0].Attributes.ContainsKey("owner").Should().BeFalse();
    }

    [UnitTest]
    public async Task ProcessBatchAsync_Insert_StampsTheOwner()
    {
        var (reader, writer, applyCount, queryCount, written) = CreateTrackedStores();
        var sut = CreateSut(reader, writer);

        var response = await sut.ProcessBatchAsync(
            EditorContext(reader, writer, OwnerPolicy, "alice"),
            new ODataBatchRequest { Requests = [CreateRequest("create-city")] },
            "https://example.test",
            CancellationToken.None);

        response.Responses.Should().ContainSingle();
        response.Responses[0].Status.Should().Be(201);
        applyCount.Value.Should().Be(1);
        queryCount.Value.Should().Be(0);
        written.Value.Creates.Should().ContainSingle();
        written.Value.Creates[0].Attributes["owner"].Should().Be("alice");
    }

    [UnitTest]
    public async Task ProcessBatchAsync_DisabledPolicy_KeepsFullEditBehavior()
    {
        var (reader, writer, applyCount, queryCount, _) = CreateTrackedStores();
        var sut = CreateSut(reader, writer);
        var disabled = OwnerPolicy with { Enabled = false };

        var response = await sut.ProcessBatchAsync(
            EditorContext(reader, writer, disabled, "bob"),
            new ODataBatchRequest { Requests = [UpdateRequest("update-city")] },
            "https://example.test",
            CancellationToken.None);

        response.Responses.Should().ContainSingle();
        response.Responses[0].Status.Should().Be(200);
        applyCount.Value.Should().Be(1);
        queryCount.Value.Should().Be(0);
    }

    private static DefaultHttpContext EditorContext(
        IFeatureReader reader,
        IFeatureWriter writer,
        MetadataV2OwnerEditPolicy? policy,
        string subject)
        => CreateContext(reader, writer, policy, subject, EditorRoles, "editor");

    private static ODataBatchRequestItem UpdateRequest(string id)
        => new()
        {
            Id = id,
            Method = "PATCH",
            Url = "Features(1,42)",
            AtomicityGroup = "g1",
            Body = new Dictionary<string, object?>
            {
                ["Attributes"] = new Dictionary<string, object?> { ["name"] = "After" }
            }
        };

    private static ODataBatchRequestItem CreateRequest(string id)
        => new()
        {
            Id = id,
            Method = "POST",
            Url = "Layers(1)/Features",
            AtomicityGroup = "g1",
            Body = new Dictionary<string, object?>
            {
                ["LayerId"] = 1,
                ["Attributes"] = new Dictionary<string, object?> { ["name"] = "Created in batch" }
            }
        };

    private static (
        IFeatureReader Reader,
        IFeatureWriter Writer,
        StrongBox<int> ApplyCount,
        StrongBox<int> QueryCount,
        StrongBox<FeatureEditBatch> Written) CreateTrackedStores()
    {
        var reader = Substitute.For<IFeatureReader>();
        var writer = Substitute.For<IFeatureWriter>();
        var applyCount = new StrongBox<int>(0);
        var queryCount = new StrongBox<int>(0);
        var written = new StrongBox<FeatureEditBatch>(default);
        reader.GetAsync(1, Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<Feature?>(CreateFeature(call.ArgAt<long>(1), "visible")));
        reader.QueryAsync(default, default, default)
            .ReturnsForAnyArgs(call =>
            {
                queryCount.Value++;
                var query = call.ArgAt<FeatureQuery>(1);
                var features = ImmutableArray.CreateBuilder<Feature>();
                if (query.ObjectIds is { } ids)
                {
                    foreach (var id in ids)
                    {
                        features.Add(Feature.Create(
                            id,
                            null,
                            ImmutableDictionary.CreateRange(new Dictionary<string, object?> { ["owner"] = "alice" })));
                    }
                }

                return QueryResult<Feature>.Create(features.Count, features.ToImmutable());
            });
        writer.ApplyEditsAsync(default, default!, default)
            .ReturnsForAnyArgs(call =>
            {
                applyCount.Value++;
                var batch = call.ArgAt<FeatureEditBatch>(1);
                written.Value = batch;
                var created = batch.Creates.IsDefaultOrEmpty ? 0 : batch.Creates.Length;
                return FeatureEditResult.Success(
                    createdCount: created,
                    updatedCount: batch.Updates.IsDefaultOrEmpty ? 0 : batch.Updates.Length,
                    deletedCount: batch.Deletes.IsDefaultOrEmpty ? 0 : batch.Deletes.Length,
                    createdIds: created > 0 ? ImmutableArray.Create(101L) : default,
                    createResults: created > 0
                        ? ImmutableArray.Create(EditOperationResult.Success(101))
                        : default,
                    updateResults: batch.Updates.IsDefaultOrEmpty
                        ? default
                        : ImmutableArray.Create(EditOperationResult.Success(42)),
                    deleteResults: batch.Deletes.IsDefaultOrEmpty
                        ? default
                        : ImmutableArray.Create(EditOperationResult.Success(42)));
            });
        return (reader, writer, applyCount, queryCount, written);
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

    private static DefaultHttpContext CreateContext(
        IFeatureReader featureReader,
        IFeatureWriter featureWriter,
        params string[] roles)
        => CreateContext(featureReader, featureWriter, ownerPolicy: null, subject: null, rbac: null, roles);

    private static DefaultHttpContext CreateContext(
        IFeatureReader featureReader,
        IFeatureWriter featureWriter,
        MetadataV2OwnerEditPolicy? ownerPolicy,
        string? subject,
        RbacOptions? rbac,
        params string[] roles)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAccessPolicyEvaluator, AccessPolicyEvaluator>();
        services.AddSingleton<IOptions<RbacOptions>>(Options.Create(rbac ?? new RbacOptions()));
        services.AddSingleton<IMetadataV2GraphProvider>(CreateMetadataProvider(ownerPolicy));
        services.AddSingleton(new ODataFeatureProviderResolver(
            featureReader,
            featureWriter,
            providerQueryRouter: null));

        var context = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider()
        };

        var claims = new List<Claim>();
        if (!string.IsNullOrEmpty(subject))
        {
            claims.Add(new Claim(ClaimTypes.NameIdentifier, subject));
        }

        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));
        if (claims.Count > 0)
        {
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

    private static TestMetadataV2GraphProvider CreateMetadataProvider(MetadataV2OwnerEditPolicy? ownerPolicy = null)
    {
        var builder = new TestMetadataV2GraphBuilder()
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
                publicationType: MetadataV2PublicationType.ODataEntitySet);
        if (ownerPolicy is not null)
        {
            builder = builder.WithOwnerEditPolicy("res-layer-1", ownerPolicy);
        }

        return builder.BuildProvider();
    }

    private static Feature CreateFeature(long id, string name)
        => Feature.Create(
            id,
            geometry: null,
            ImmutableDictionary<string, object?>.Empty.Add("name", name));
}
