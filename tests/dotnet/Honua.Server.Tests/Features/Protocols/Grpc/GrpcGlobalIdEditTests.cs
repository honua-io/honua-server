// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Grpc.Core;
using Honua.Core.Configuration;
using Honua.Core.Features.Authorization;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Events.Outbox;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.Security;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Validation;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Core.Queries.Filters;
using Honua.Server.Features.Protocols.Grpc;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Events;
using Honua.Infrastructure.Services;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using NSubstitute;
using Proto = Geospatial.V1;

namespace Honua.Server.Tests.Features.Protocols.Grpc;

// use_global_ids edits pass the same authorization, capability and visibility gates as
// object-ID edits, and resolve targets through the RLS-enforced reader before any write.
[Protocol(TestProtocols.Grpc)]
[Operation(Operations.ApplyEdits)]
public sealed class GrpcGlobalIdEditTests : IDisposable
{
    private const string GlobalId = "6f1c2d3e-4b5a-4c6d-8e7f-0a1b2c3d4e5f";
    private const long ResolvedObjectId = 42;

    private readonly IResourceValidator _resourceValidator = Substitute.For<IResourceValidator>();
    private readonly IFeatureReader _featureReader = Substitute.For<IFeatureReader>();
    private readonly IFeatureWriter _featureWriter = Substitute.For<IFeatureWriter>();
    private readonly GrpcApplyEditsReceipts _receipts = new();
    private readonly List<GrpcFeatureServiceTests.TestServerCallContext> _contexts = [];
    private readonly HonuaFeatureService _sut;

    public GrpcGlobalIdEditTests()
    {
        var crsRegistry = Substitute.For<ICrsRegistry>();
#pragma warning disable CA2012 // NSubstitute setup for ValueTask-returning members.
        crsRegistry.IsSridSupportedAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(_ => new ValueTask<bool>(true));
#pragma warning restore CA2012
        _sut = new HonuaFeatureService(
            _resourceValidator, _featureReader, _featureWriter, Substitute.For<IStreamingFeatureStore>(),
            new CommonQueryValidator(Options.Create(new LimitsOptions())),
            new SpatialReferenceResolver(Substitute.For<ICrsDetectionService>(), crsRegistry),
            new FeatureMutationEventService(
                Substitute.For<IFeatureChangeEventPublisher>(),
                outboxCapabilityProvider: Substitute.For<IOutboxCapabilityProvider>()),
            Options.Create(new LimitsOptions()),
            Options.Create(new GrpcOptions()),
            NullLogger<HonuaFeatureService>.Instance,
            _receipts);
        UseLayer(GlobalIdResource(hidden: false), capabilities: null);
        _featureReader.GetAsync(Arg.Any<int>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<Feature?>(Feature.Create(call.ArgAt<long>(1), geometry: null)));
        _featureReader.QueryAsync(Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(QueryResult<Feature>.Create(1, [Feature.Create(ResolvedObjectId, null, StoredGlobalId())]));
    }

    [UnitTest]
    [Endpoint("POST /geospatial.v1.FeatureService/ApplyEdits")]
    public async Task DeleteByGlobalId_WithoutDeleteGrant_IsDeniedBeforeAnyRead()
    {
        var act = async () => await _sut.ApplyEdits(DeleteRequest(), CallContext("update"));

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
        AssertNothingRead();
    }

    [UnitTest]
    [Endpoint("POST /geospatial.v1.FeatureService/ApplyEdits")]
    public async Task DeleteByGlobalId_WithoutDeleteCapability_FailsPreconditionBeforeAnyRead()
    {
        UseLayer(GlobalIdResource(hidden: false), capabilities: ["Query", "Update"]);

        var act = async () => await _sut.ApplyEdits(DeleteRequest(), CallContext("update", "delete"));

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.FailedPrecondition);
        AssertNothingRead();
    }

    [UnitTest]
    [Endpoint("POST /geospatial.v1.FeatureService/ApplyEdits")]
    public async Task DeleteByGlobalId_ResolvesTheTargetAndReportsItsGlobalId()
    {
        _featureWriter.ApplyEditsAsync(default, default, default).ReturnsForAnyArgs(Task.FromResult(
            FeatureEditResult.Success(0, 0, 1, deleteResults: [EditOperationResult.Success(ResolvedObjectId)])));

        var response = await _sut.ApplyEdits(DeleteRequest(), CallContext("delete"));

        var written = (FeatureEditBatch)_featureWriter.ReceivedCalls()
            .Single(call => call.GetMethodInfo().Name == nameof(IFeatureWriter.ApplyEditsAsync))
            .GetArguments()[1]!;
        written.Deletes.Should().Equal(ResolvedObjectId);
        response.DeleteResults.Should().ContainSingle().Which.GlobalId.Should().Be(GlobalId);
    }

    [UnitTest]
    [Endpoint("POST /geospatial.v1.FeatureService/ApplyEdits")]
    public async Task DeleteByGlobalId_MissingTarget_FailsNotFoundWithoutWriting()
    {
        _featureReader.QueryAsync(Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(QueryResult<Feature>.Empty());

        var act = async () => await _sut.ApplyEdits(DeleteRequest(), CallContext("delete"));

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.NotFound);
        AssertNothingWritten();
    }

    [UnitTest]
    [Endpoint("POST /geospatial.v1.FeatureService/ApplyEdits")]
    public async Task GlobalIdSelectors_OnHiddenGlobalIdField_FailPreconditionWithoutReading()
    {
        UseLayer(GlobalIdResource(hidden: true), capabilities: null);

        var act = async () => await _sut.ApplyEdits(DeleteRequest(), CallContext("delete"));

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.FailedPrecondition);
        AssertNothingRead();
    }

    [UnitTest]
    [Endpoint("POST /geospatial.v1.FeatureService/ApplyEdits")]
    public async Task AddWithExistingGlobalId_IsRejectedWithoutWriting()
    {
        var request = new Proto.ApplyEditsRequest { ServiceId = "test", LayerId = 0 };
        request.Adds.Add(new Proto.Feature { GlobalId = GlobalId });

        var act = async () => await _sut.ApplyEdits(request, CallContext("insert"));

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        AssertNothingWritten();
    }

    [UnitTest]
    [Endpoint("POST /geospatial.v1.FeatureService/ApplyEdits")]
    public async Task AddWithoutGlobalId_GetsAServerAssignedIdentity()
    {
        _featureReader.QueryAsync(Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(QueryResult<Feature>.Empty());
        _featureWriter.ApplyEditsAsync(default, default, default).ReturnsForAnyArgs(Task.FromResult(
            FeatureEditResult.Success(1, 0, 0, createResults: [EditOperationResult.Success(7)])));
        var request = new Proto.ApplyEditsRequest { ServiceId = "test", LayerId = 0 };
        request.Adds.Add(new Proto.Feature());

        var response = await _sut.ApplyEdits(request, CallContext("insert"));

        var written = (FeatureEditBatch)_featureWriter.ReceivedCalls()
            .Single(call => call.GetMethodInfo().Name == nameof(IFeatureWriter.ApplyEditsAsync))
            .GetArguments()[1]!;
        var assigned = written.Creates.Single().Attributes["GlobalID"].Should().BeOfType<string>().Subject;
        Guid.TryParseExact(assigned, "D", out _).Should().BeTrue();
        response.AddResults.Single().GlobalId.Should().Be(assigned);
    }

    [UnitTest]
    [Endpoint("POST /geospatial.v1.FeatureService/ApplyEdits")]
    public async Task AddsRepeatingAGlobalIdThroughRawAttributes_AreRejectedWithoutWriting()
    {
        var request = new Proto.ApplyEditsRequest { ServiceId = "test", LayerId = 0 };
        var raw = new Proto.Feature();
        raw.Attributes["GlobalID"] = new Proto.AttributeValue { StringValue = "{" + GlobalId.ToUpperInvariant() + "}" };
        request.Adds.Add(raw);
        request.Adds.Add(new Proto.Feature { GlobalId = GlobalId });

        var act = async () => await _sut.ApplyEdits(request, CallContext("insert"));

        (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        AssertNothingWritten();
    }

    [UnitTest]
    [Endpoint("POST /geospatial.v1.FeatureService/ApplyEdits")]
    public async Task MaskedGlobalIdField_ExposesNoIdsAndRefusesSelectors()
    {
        var masks = Substitute.For<IFieldMaskSource>();
        masks.ResolveAsync(Arg.Any<MetadataV2Resource>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(ImmutableArray.Create("GLOBALID")));
        _featureWriter.ApplyEditsAsync(default, default, default).ReturnsForAnyArgs(Task.FromResult(
            FeatureEditResult.Success(1, 0, 0, createResults: [EditOperationResult.Success(7, GlobalId)])));
        var add = new Proto.ApplyEditsRequest { ServiceId = "test", LayerId = 0 };
        add.Adds.Add(new Proto.Feature());

        var added = await _sut.ApplyEdits(add, MaskedContext(masks, "insert"));
        var selector = async () => await _sut.ApplyEdits(DeleteRequest(), MaskedContext(masks, "delete"));

        added.AddResults.Should().ContainSingle().Which.GlobalId.Should().BeEmpty();
        (await selector.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.FailedPrecondition);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /geospatial.v1.FeatureService/QueryFeatures")]
    public async Task MaskedGlobalIdField_IsAbsentFromQueryResults()
    {
        var masks = Substitute.For<IFieldMaskSource>();
        masks.ResolveAsync(Arg.Any<MetadataV2Resource>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(ImmutableArray.Create("globalid")));

        var response = await _sut.QueryFeatures(
            new Proto.QueryFeaturesRequest { ServiceId = "test", LayerId = 0, ReturnGeometry = false },
            MaskedContext(masks));

        response.GlobalIdFieldName.Should().BeEmpty();
        response.Features.Should().OnlyContain(feature => feature.GlobalId.Length == 0);
    }

    private GrpcFeatureServiceTests.TestServerCallContext MaskedContext(IFieldMaskSource masks, params string[] operations)
    {
        var context = GrpcFeatureServiceTests.CreateCallContext(
            GrpcFeatureServiceTests.CreateAuthenticatedUser("editor"),
            tenantId: null,
            [.. operations.DefaultIfEmpty("query").Select(operation => new PermissionGrant { Service = "test", Layer = "*", Operation = operation })],
            services => services.AddSingleton(masks));
        _contexts.Add(context);
        return context;
    }

    private void UseLayer(MetadataV2Resource resource, IReadOnlyList<string>? capabilities)
    {
        var triple = GrpcFeatureServiceTests.CreateTriple(GrpcFeatureServiceTests.CreateService("test"), resource);
        if (capabilities is not null)
        {
            triple = triple with { Publication = triple.Publication with { Capabilities = capabilities } };
        }

        _resourceValidator.ValidateServiceLayerV2Async("test", 0, Arg.Any<CancellationToken>())
            .Returns(ResourceValidationResult.Success(triple));
    }

    private static MetadataV2Resource GlobalIdResource(bool hidden)
        => new()
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "resource-test", Name = "test" },
            Spatial = new MetadataV2ResourceSpatial
            {
                GeometryType = MetadataV2GeometryType.Point,
                SpatialReference = MetadataV2SpatialReference.Wgs84
            },
            Editing = new MetadataV2ResourceEditing { GlobalIdField = "GlobalID" },
            SchemaFields =
            [
                new MetadataV2Field { Name = "objectid", Type = MetadataV2FieldType.Integer, Nullable = false, SemanticRoles = ["id.primary"] },
                new MetadataV2Field { Name = "GlobalID", Type = MetadataV2FieldType.Uuid, Nullable = true, Hidden = hidden },
            ]
        };

    private static ImmutableDictionary<string, object?> StoredGlobalId()
        => ImmutableDictionary<string, object?>.Empty.Add("GlobalID", Guid.Parse(GlobalId));

    private static Proto.ApplyEditsRequest DeleteRequest()
    {
        var request = new Proto.ApplyEditsRequest { ServiceId = "test", LayerId = 0, UseGlobalIds = true };
        request.DeleteGlobalIds.Add(GlobalId.ToUpperInvariant());
        return request;
    }

    public void Dispose()
    {
        foreach (var context in _contexts)
        {
            context.Dispose();
        }

        _receipts.Dispose();
    }

    private GrpcFeatureServiceTests.TestServerCallContext CallContext(params string[] operations)
    {
        var context = GrpcFeatureServiceTests.CreateCallContext(
            GrpcFeatureServiceTests.CreateAuthenticatedUser("editor"),
            [.. operations.Select(operation => new PermissionGrant { Service = "test", Layer = "*", Operation = operation })]);
        _contexts.Add(context);
        return context;
    }

    private void AssertNothingRead()
    {
        _featureReader.ReceivedCalls().Should().BeEmpty();
        AssertNothingWritten();
    }

    private void AssertNothingWritten()
        => _featureWriter.ReceivedCalls()
            .Should().NotContain(call => call.GetMethodInfo().Name == nameof(IFeatureWriter.ApplyEditsAsync));
}
