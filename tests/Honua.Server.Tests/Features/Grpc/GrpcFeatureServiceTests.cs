// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Security.Claims;
using FluentAssertions;
using Grpc.Core;
using Honua.Core.Configuration;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Security;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Validation;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Server.Features.Grpc;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Services;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using NSubstitute;
using Proto = Geospatial.V1;

namespace Honua.Server.Tests.Features.Grpc;

[Protocol(Protocols.Grpc)]
[Operation(Operations.Query)]
public sealed class GrpcFeatureServiceTests
{
    private readonly IResourceValidator _resourceValidator = Substitute.For<IResourceValidator>();
    private readonly IFeatureReader _featureReader = Substitute.For<IFeatureReader>();
    private readonly IFeatureWriter _featureWriter = Substitute.For<IFeatureWriter>();
    private readonly IStreamingFeatureStore _streamingStore = Substitute.For<IStreamingFeatureStore>();
    private readonly ICrsDetectionService _crsDetectionService = Substitute.For<ICrsDetectionService>();
    private readonly ICrsRegistry _crsRegistry = Substitute.For<ICrsRegistry>();
    private readonly HonuaFeatureService _sut;

    private static readonly LayerDefinition _testLayer = new(
        Id: 0,
        Name: "test",
        Description: null,
        GeometryType: Core.Features.Catalog.Domain.GeometryType.Point,
        SpatialReference: SpatialReference.WGS84,
        Fields: new[]
        {
            new FieldDefinition("objectid", FieldType.Integer, Nullable: false),
            new FieldDefinition("name", FieldType.String, Length: 255)
        });

    private static readonly ServiceDefinition _testService = new(
        "test", "test", new[] { _testLayer }, SpatialReference.WGS84);

    public GrpcFeatureServiceTests()
    {
#pragma warning disable CA2012 // NSubstitute setup for ValueTask-returning members.
        _crsRegistry
            .IsSridSupportedAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ => new ValueTask<bool>(true));
#pragma warning restore CA2012

        _sut = new HonuaFeatureService(
            _resourceValidator, _featureReader, _featureWriter, _streamingStore,
            new CommonQueryValidator(Options.Create(new LimitsOptions())),
            new SpatialReferenceResolver(_crsDetectionService, _crsRegistry),
            Options.Create(new LimitsOptions()),
            Options.Create(new GrpcOptions()),
            NullLogger<HonuaFeatureService>.Instance);

        // Default: valid service/layer
        _resourceValidator
            .ValidateServiceLayerAsync("test", 0, Arg.Any<CancellationToken>())
            .Returns(ResourceValidationResult.Success((_testService, _testLayer)));
    }

    [UnitTest]
    [Endpoint("POST /grpc/honua.v1.FeatureService/ApplyEdits")]
    [Operation(Operations.ApplyEdits)]
    public async Task ApplyEdits_WithValidRequest_AppliesEditsAndReturnsResponse()
    {
        var applyResult = FeatureEditResult.Success(
            createdCount: 1,
            updatedCount: 1,
            deletedCount: 1,
            createResults: ImmutableArray.Create(EditOperationResult.Success(101)),
            updateResults: ImmutableArray.Create(EditOperationResult.Success(11)),
            deleteResults: ImmutableArray.Create(EditOperationResult.Success(12)));

        _featureWriter
            .ApplyEditsAsync(default, default, default)
            .ReturnsForAnyArgs(Task.FromResult(applyResult));

        var request = new Proto.ApplyEditsRequest
        {
            ServiceId = "test",
            LayerId = 0,
            RollbackOnFailure = true
        };
        request.Adds.Add(new Proto.Feature
        {
            Attributes = { ["name"] = new Proto.AttributeValue { StringValue = "new feature" } }
        });
        request.Updates.Add(new Proto.Feature
        {
            Id = 11,
            Attributes = { ["name"] = new Proto.AttributeValue { StringValue = "updated feature" } }
        });
        request.Deletes.Add(12);

        var response = await _sut.ApplyEdits(request, CreateCallContext());

        response.AddResults.Should().ContainSingle();
        response.UpdateResults.Should().ContainSingle();
        response.DeleteResults.Should().ContainSingle();
        response.AddResults[0].ObjectId.Should().Be(101);
        response.UpdateResults[0].ObjectId.Should().Be(11);
        response.DeleteResults[0].ObjectId.Should().Be(12);

        var applyEditsCalls = _featureWriter.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(IFeatureWriter.ApplyEditsAsync))
            .ToArray();
        applyEditsCalls.Should().HaveCount(1);

        var arguments = applyEditsCalls[0].GetArguments();
        arguments[0].Should().Be(0);
        arguments[1].Should().NotBeNull();

        var batch = (FeatureEditBatch)arguments[1]!;
        batch.RollbackOnFailure.Should().BeTrue();
        batch.Creates.Length.Should().Be(1);
        batch.Updates.Length.Should().Be(1);
        batch.Deletes.SequenceEqual(new long[] { 12 }).Should().BeTrue();
    }

    [UnitTest]
    [Endpoint("POST /grpc/honua.v1.FeatureService/ApplyEdits")]
    [Operation(Operations.ApplyEdits)]
    public async Task ApplyEdits_WithMissingUpdateObjectId_ThrowsInvalidArgument()
    {
        var request = new Proto.ApplyEditsRequest
        {
            ServiceId = "test",
            LayerId = 0
        };
        request.Updates.Add(new Proto.Feature
        {
            Attributes = { ["name"] = new Proto.AttributeValue { StringValue = "missing id" } }
        });

        var act = async () => await _sut.ApplyEdits(request, CreateCallContext());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Which.Status.Detail.Should().Contain("positive id");
        _featureWriter.ReceivedCalls()
            .Should()
            .NotContain(call => call.GetMethodInfo().Name == nameof(IFeatureWriter.ApplyEditsAsync));
    }

    [UnitTest]
    [Endpoint("POST /grpc/honua.v1.FeatureService/ApplyEdits")]
    [Operation(Operations.ApplyEdits)]
    public async Task ApplyEdits_GrpcDisabled_ThrowsNotFoundRpcException()
    {
        var grpcDisabledService = _testService with
        {
            Metadata = new CatalogMetadata
            {
                EnabledProtocols = [ServiceProtocols.FeatureServer]
            }
        };

        _resourceValidator
            .ValidateServiceLayerAsync("grpc-disabled", 0, Arg.Any<CancellationToken>())
            .Returns(ResourceValidationResult.Success((grpcDisabledService, _testLayer)));

        var request = new Proto.ApplyEditsRequest
        {
            ServiceId = "grpc-disabled",
            LayerId = 0
        };

        var act = async () => await _sut.ApplyEdits(request, CreateCallContext());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.NotFound);
        ex.Which.Status.Detail.Should().Be("Grpc is not enabled for this service.");
    }

    [UnitTest]
    [Endpoint("POST /grpc/honua.v1.FeatureService/ApplyEdits")]
    [Operation(Operations.ApplyEdits)]
    public async Task ApplyEdits_WithoutAuthentication_ThrowsUnauthenticatedRpcException()
    {
        var request = new Proto.ApplyEditsRequest
        {
            ServiceId = "test",
            LayerId = 0
        };
        request.Adds.Add(new Proto.Feature
        {
            Attributes = { ["name"] = new Proto.AttributeValue { StringValue = "test" } }
        });

        var act = async () => await _sut.ApplyEdits(request, CreateCallContext(CreateAnonymousUser()));

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.Unauthenticated);
    }

    [UnitTest]
    [Endpoint("POST /grpc/honua.v1.FeatureService/ApplyEdits")]
    [Operation(Operations.ApplyEdits)]
    public async Task ApplyEdits_WithoutDataEditorRole_ThrowsPermissionDeniedRpcException()
    {
        var request = new Proto.ApplyEditsRequest
        {
            ServiceId = "test",
            LayerId = 0
        };
        request.Adds.Add(new Proto.Feature
        {
            Attributes = { ["name"] = new Proto.AttributeValue { StringValue = "test" } }
        });

        // Authenticated user with no matching roles
        var act = async () => await _sut.ApplyEdits(request, CreateCallContext(CreateAuthenticatedUser("viewer")));

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
    }

    [UnitTest]
    [Endpoint("POST /grpc/honua.v1.FeatureService/ApplyEdits")]
    [Operation(Operations.ApplyEdits)]
    public async Task ApplyEdits_AnonymousWritePolicyWithoutAuthentication_ThrowsUnauthenticatedRpcException()
    {
        var anonymousWriteService = _testService with
        {
            Metadata = new CatalogMetadata
            {
                AccessPolicy = new AccessPolicy
                {
                    AllowAnonymousWrite = true
                }
            }
        };

        _resourceValidator
            .ValidateServiceLayerAsync("anonymous-write", 0, Arg.Any<CancellationToken>())
            .Returns(ResourceValidationResult.Success((anonymousWriteService, _testLayer)));

        var request = new Proto.ApplyEditsRequest
        {
            ServiceId = "anonymous-write",
            LayerId = 0
        };
        request.Adds.Add(new Proto.Feature
        {
            Attributes = { ["name"] = new Proto.AttributeValue { StringValue = "test" } }
        });

        var act = async () => await _sut.ApplyEdits(request, CreateCallContext(CreateAnonymousUser()));

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.Unauthenticated);
    }

    [UnitTest]
    [Endpoint("POST /grpc/honua.v1.FeatureService/QueryFeatures")]
    public async Task QueryFeatures_WithWhereClause_ReturnsFeatures()
    {
        var features = ImmutableArray.Create(
            Feature.Create(1, null, ImmutableDictionary<string, object?>.Empty
                .Add("name", "A")));
        _featureReader.QueryAsync(0, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(QueryResult<Feature>.Create(1, features));

        var request = new Proto.QueryFeaturesRequest
        {
            ServiceId = "test",
            LayerId = 0,
            Where = "name = 'A'"
        };

        var response = await _sut.QueryFeatures(request, CreateCallContext());

        response.ObjectIdFieldName.Should().Be("objectid");
        response.Features.Should().HaveCount(1);
        response.Features[0].Id.Should().Be(1);
        response.Features[0].Attributes["name"].StringValue.Should().Be("A");
    }

    [UnitTest]
    [Endpoint("POST /grpc/honua.v1.FeatureService/QueryFeatures")]
    public async Task QueryFeatures_CountOnly_ReturnsCountWithNoFeatures()
    {
        _featureReader.CountAsync(0, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(42L);

        var request = new Proto.QueryFeaturesRequest
        {
            ServiceId = "test",
            LayerId = 0,
            Where = "1=1",
            ReturnCountOnly = true
        };

        var response = await _sut.QueryFeatures(request, CreateCallContext());

        response.Count.Should().Be(42);
        response.Features.Should().BeEmpty();
    }

    [UnitTest]
    [Endpoint("POST /grpc/honua.v1.FeatureService/QueryFeatures")]
    public async Task QueryFeatures_IdsOnly_ReturnsObjectIds()
    {
        _featureReader.QueryObjectIdsAsync(0, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(ImmutableArray.Create<long>(1, 2, 3));

        var request = new Proto.QueryFeaturesRequest
        {
            ServiceId = "test",
            LayerId = 0,
            ReturnIdsOnly = true
        };

        var response = await _sut.QueryFeatures(request, CreateCallContext());

        response.ObjectIds.Should().BeEquivalentTo(new long[] { 1, 2, 3 });
        response.Features.Should().BeEmpty();
        await _featureReader.Received(1).QueryObjectIdsAsync(0, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>());
        _ = _featureReader.DidNotReceive().QueryAsync(0, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Endpoint("POST /grpc/honua.v1.FeatureService/QueryFeatures")]
    public async Task QueryFeatures_ExtentOnly_ReturnsExtent()
    {
        _featureReader.GetExtentAsync(0, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(FeatureExtent.Create(-10, -20, 30, 40, 4326));

        var request = new Proto.QueryFeaturesRequest
        {
            ServiceId = "test",
            LayerId = 0,
            ReturnExtentOnly = true
        };

        var response = await _sut.QueryFeatures(request, CreateCallContext());

        response.Extent.Should().NotBeNull();
        response.Extent.Xmin.Should().Be(-10);
        response.Extent.Ymin.Should().Be(-20);
        response.Extent.Xmax.Should().Be(30);
        response.Extent.Ymax.Should().Be(40);
        response.Features.Should().BeEmpty();
    }

    [UnitTest]
    [Endpoint("POST /grpc/honua.v1.FeatureService/QueryFeatures")]
    public async Task QueryFeatures_InvalidService_ThrowsNotFoundRpcException()
    {
        _resourceValidator
            .ValidateServiceLayerAsync("bad", 0, Arg.Any<CancellationToken>())
            .Returns(ResourceValidationResult.NotFound<(ServiceDefinition, LayerDefinition)>(
                "Service 'bad' not found"));

        var request = new Proto.QueryFeaturesRequest
        {
            ServiceId = "bad",
            LayerId = 0
        };

        var act = async () => await _sut.QueryFeatures(request, CreateCallContext());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.NotFound);
    }

    [UnitTest]
    [Endpoint("POST /grpc/honua.v1.FeatureService/QueryFeatures")]
    public async Task QueryFeatures_GrpcDisabled_ThrowsNotFoundRpcException()
    {
        var grpcDisabledService = _testService with
        {
            Metadata = new CatalogMetadata
            {
                EnabledProtocols = [ServiceProtocols.FeatureServer]
            }
        };

        _resourceValidator
            .ValidateServiceLayerAsync("grpc-disabled", 0, Arg.Any<CancellationToken>())
            .Returns(ResourceValidationResult.Success((grpcDisabledService, _testLayer)));

        var request = new Proto.QueryFeaturesRequest
        {
            ServiceId = "grpc-disabled",
            LayerId = 0
        };

        var act = async () => await _sut.QueryFeatures(request, CreateCallContext());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.NotFound);
        ex.Which.Status.Detail.Should().Be("Grpc is not enabled for this service.");
    }

    [UnitTest]
    [Endpoint("POST /grpc/honua.v1.FeatureService/QueryFeatures")]
    public async Task QueryFeatures_ExceededTransferLimit_SetsFlag()
    {
        var features = ImmutableArray.Create(Feature.Create(1, null));
        _featureReader.QueryAsync(0, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(QueryResult<Feature>.Create(100, features, hasMoreResults: true));

        var request = new Proto.QueryFeaturesRequest
        {
            ServiceId = "test",
            LayerId = 0,
            ResultRecordCount = 1
        };

        var response = await _sut.QueryFeatures(request, CreateCallContext());

        response.ExceededTransferLimit.Should().BeTrue();
    }

    [UnitTest]
    [Endpoint("POST /grpc/honua.v1.FeatureService/QueryFeatures")]
    public async Task QueryFeatures_ReturnGeometryFalse_OmitsGeometryFromResponse()
    {
        var geometry = new WKBWriter().Write(new Point(10, 20));
        var features = ImmutableArray.Create(Feature.Create(1, geometry));
        _featureReader.QueryAsync(0, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(QueryResult<Feature>.Create(1, features));

        var request = new Proto.QueryFeaturesRequest
        {
            ServiceId = "test",
            LayerId = 0,
            ReturnGeometry = false
        };

        var response = await _sut.QueryFeatures(request, CreateCallContext());

        response.Features.Should().HaveCount(1);
        response.Features[0].Geometry.Should().BeNull();
    }

    [UnitTest]
    [Endpoint("POST /grpc/honua.v1.FeatureService/QueryFeatures")]
    public async Task QueryFeatures_WithOutSr_UsesRequestedSpatialReference()
    {
        var features = ImmutableArray.Create(Feature.Create(1, null));
        _featureReader.QueryAsync(0, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(QueryResult<Feature>.Create(1, features));

        var request = new Proto.QueryFeaturesRequest
        {
            ServiceId = "test",
            LayerId = 0,
            OutSr = new Proto.SpatialReference { Wkid = 3857 }
        };

        var response = await _sut.QueryFeatures(request, CreateCallContext());

        response.SpatialReference.Wkid.Should().Be(3857);
        await _featureReader.Received(1).QueryAsync(
            0,
            Arg.Is<FeatureQuery>(q =>
                q.OutputSrid == 3857 &&
                q.SpatialReferenceSrid == _testLayer.SpatialReference.ToSrid()),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Endpoint("POST /grpc/honua.v1.FeatureService/QueryFeatures")]
    public async Task QueryFeatures_WithWktOutSr_ResolvesSridViaCrsDetection()
    {
        _crsDetectionService.DetectFromWktAsync(Arg.Any<string>())
            .Returns(Task.FromResult<int?>(3857));

        var features = ImmutableArray.Create(Feature.Create(1, null));
        _featureReader.QueryAsync(0, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(QueryResult<Feature>.Create(1, features));

        var request = new Proto.QueryFeaturesRequest
        {
            ServiceId = "test",
            LayerId = 0,
            OutSr = new Proto.SpatialReference { Wkt = "PROJCRS[\"WGS 84 / Pseudo-Mercator\"]" }
        };

        var response = await _sut.QueryFeatures(request, CreateCallContext());

        response.SpatialReference.Wkid.Should().Be(3857);
        await _featureReader.Received(1).QueryAsync(
            0,
            Arg.Is<FeatureQuery>(q => q.OutputSrid == 3857),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Endpoint("POST /grpc/honua.v1.FeatureService/QueryFeatures")]
    public async Task QueryFeatures_WithInvalidOutSr_ThrowsInvalidArgument()
    {
        var request = new Proto.QueryFeaturesRequest
        {
            ServiceId = "test",
            LayerId = 0,
            OutSr = new Proto.SpatialReference()
        };

        var act = async () => await _sut.QueryFeatures(request, CreateCallContext());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Which.Status.Detail.Should().Be("Invalid out_sr value.");
    }

    [UnitTest]
    [Endpoint("POST /grpc/honua.v1.FeatureService/QueryFeatures")]
    public async Task QueryFeatures_WithUnsupportedWkidOutSr_ThrowsInvalidArgument()
    {
#pragma warning disable CA2012 // NSubstitute setup for ValueTask-returning members.
        _crsRegistry
            .IsSridSupportedAsync(910001, Arg.Any<CancellationToken>())
            .Returns(_ => new ValueTask<bool>(false));
#pragma warning restore CA2012

        var request = new Proto.QueryFeaturesRequest
        {
            ServiceId = "test",
            LayerId = 0,
            OutSr = new Proto.SpatialReference { Wkid = 910001 }
        };

        var act = async () => await _sut.QueryFeatures(request, CreateCallContext());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Which.Status.Detail.Should().Be("Invalid out_sr value.");
    }

    [UnitTest]
    [Endpoint("POST /grpc/honua.v1.FeatureService/QueryFeatures")]
    public async Task QueryFeatures_WithoutResultRecordCount_AppliesConfiguredDefaultLimit()
    {
        var features = ImmutableArray.Create(Feature.Create(1, null));
        _featureReader.QueryAsync(0, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(QueryResult<Feature>.Create(1, features));

        var request = new Proto.QueryFeaturesRequest
        {
            ServiceId = "test",
            LayerId = 0
        };

        await _sut.QueryFeatures(request, CreateCallContext());

        await _featureReader.Received(1).QueryAsync(
            0,
            Arg.Is<FeatureQuery>(q => q.Limit == new LimitsOptions().Query.DefaultRecordCount),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Endpoint("POST /grpc/honua.v1.FeatureService/QueryFeatures")]
    public async Task QueryFeatures_LimitAboveConfiguredMaximum_ThrowsInvalidArgument()
    {
        var request = new Proto.QueryFeaturesRequest
        {
            ServiceId = "test",
            LayerId = 0,
            ResultRecordCount = new LimitsOptions().Query.MaxRecordCount + 1
        };

        var act = async () => await _sut.QueryFeatures(request, CreateCallContext());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Which.Status.Detail.Should().Contain("resultRecordCount");
    }

    [UnitTest]
    [Endpoint("POST /grpc/honua.v1.FeatureService/QueryFeaturesStream")]
    public async Task QueryFeaturesStream_StreamsPages()
    {
        var features = Enumerable.Range(1, 5)
            .Select(i => Feature.Create(i, null))
            .ToAsyncEnumerable();

        _streamingStore.StreamFeaturesAsync(0, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(features);

        var request = new Proto.QueryFeaturesRequest
        {
            ServiceId = "test",
            LayerId = 0,
            Where = "1=1"
        };

        var writer = new TestServerStreamWriter<Proto.FeaturePage>();
        await _sut.QueryFeaturesStream(request, writer, CreateCallContext());

        writer.Pages.Should().NotBeEmpty();
        writer.Pages.Last().IsLastPage.Should().BeTrue();

        // First page has metadata
        writer.Pages[0].ObjectIdFieldName.Should().Be("objectid");
        writer.Pages[0].GeometryType.Should().Be(Proto.GeometryType.Point);
        writer.Pages[0].Fields.Should().HaveCount(2); // objectid + name (both non-geometry)

        // All features accounted for
        var totalFeatures = writer.Pages.Sum(p => p.Features.Count);
        totalFeatures.Should().Be(5);
    }

    [UnitTest]
    [Endpoint("POST /grpc/honua.v1.FeatureService/QueryFeatures")]
    public async Task QueryFeatures_WithUnsupportedSpatialFilterWkid_ThrowsInvalidArgument()
    {
#pragma warning disable CA2012 // NSubstitute setup for ValueTask-returning members.
        _crsRegistry
            .IsSridSupportedAsync(910001, Arg.Any<CancellationToken>())
            .Returns(_ => new ValueTask<bool>(false));
#pragma warning restore CA2012

        var request = new Proto.QueryFeaturesRequest
        {
            ServiceId = "test",
            LayerId = 0,
            SpatialFilter = new Proto.SpatialFilter
            {
                SpatialRelationship = Proto.SpatialRelationship.Intersects,
                SpatialReference = new Proto.SpatialReference { Wkid = 910001 },
                Geometry = new Proto.Geometry
                {
                    Point = new Proto.PointGeometry { X = -157.85, Y = 21.30 }
                }
            }
        };

        var act = async () => await _sut.QueryFeatures(request, CreateCallContext());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Which.Status.Detail.Should().Be("Invalid spatial_filter.spatial_reference value.");
    }

    [UnitTest]
    [Endpoint("POST /grpc/honua.v1.FeatureService/QueryFeaturesStream")]
    public async Task QueryFeaturesStream_ExactBatchSize_DoesNotEmitEmptyTerminalPage()
    {
        var features = Enumerable.Range(1, 1000)
            .Select(i => Feature.Create(i, null))
            .ToAsyncEnumerable();

        _streamingStore.StreamFeaturesAsync(0, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(features);

        var request = new Proto.QueryFeaturesRequest
        {
            ServiceId = "test",
            LayerId = 0,
            ReturnGeometry = true
        };

        var writer = new TestServerStreamWriter<Proto.FeaturePage>();
        await _sut.QueryFeaturesStream(request, writer, CreateCallContext());

        writer.Pages.Should().HaveCount(1);
        writer.Pages[0].IsLastPage.Should().BeTrue();
        writer.Pages[0].Features.Should().HaveCount(1000);
    }

    [UnitTest]
    [Endpoint("POST /grpc/honua.v1.FeatureService/QueryFeaturesStream")]
    public async Task QueryFeaturesStream_WithBatchSizeOne_WritesOneFeaturePerPage()
    {
        var streamBatchSizeOneSut = new HonuaFeatureService(
            _resourceValidator, _featureReader, _featureWriter, _streamingStore,
            new CommonQueryValidator(Options.Create(new LimitsOptions())),
            new SpatialReferenceResolver(_crsDetectionService, _crsRegistry),
            Options.Create(new LimitsOptions()),
            Options.Create(new GrpcOptions { StreamBatchSize = 1 }),
            NullLogger<HonuaFeatureService>.Instance);

        var features = Enumerable.Range(1, 3)
            .Select(i => Feature.Create(i, null))
            .ToAsyncEnumerable();

        _streamingStore.StreamFeaturesAsync(0, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(features);

        var request = new Proto.QueryFeaturesRequest
        {
            ServiceId = "test",
            LayerId = 0
        };

        var writer = new TestServerStreamWriter<Proto.FeaturePage>();
        await streamBatchSizeOneSut.QueryFeaturesStream(request, writer, CreateCallContext());

        writer.Pages.Should().HaveCount(3);
        writer.Pages[0].Features.Should().HaveCount(1);
        writer.Pages[1].Features.Should().HaveCount(1);
        writer.Pages[2].Features.Should().HaveCount(1);
        writer.Pages[0].IsLastPage.Should().BeFalse();
        writer.Pages[1].IsLastPage.Should().BeFalse();
        writer.Pages[2].IsLastPage.Should().BeTrue();
    }

    [UnitTest]
    [Endpoint("POST /grpc/honua.v1.FeatureService/QueryFeaturesStream")]
    public async Task QueryFeaturesStream_WithCountOnlyFlag_ThrowsInvalidArgument()
    {
        var request = new Proto.QueryFeaturesRequest
        {
            ServiceId = "test",
            LayerId = 0,
            ReturnCountOnly = true
        };

        var writer = new TestServerStreamWriter<Proto.FeaturePage>();
        var act = async () => await _sut.QueryFeaturesStream(request, writer, CreateCallContext());

        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Which.Status.Detail.Should().Contain("QueryFeaturesStream");
    }

    private static TestServerCallContext CreateCallContext(ClaimsPrincipal? user = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAccessPolicyEvaluator, AccessPolicyEvaluator>();
        services.Configure<RbacOptions>(opts =>
        {
            opts.DataEditorRoles = ["data-editor"];
        });
        var serviceProvider = services.BuildServiceProvider();

        var httpContext = new DefaultHttpContext
        {
            RequestServices = serviceProvider,
            User = user ?? CreateAuthenticatedUser("admin")
        };

        var ctx = new TestServerCallContext();
        ctx.UserState["__HttpContext"] = httpContext;
        return ctx;
    }

    private static ClaimsPrincipal CreateAuthenticatedUser(params string[] roles)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, "test-user")
        };
        foreach (var role in roles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        var identity = new ClaimsIdentity(claims, "Test");
        return new ClaimsPrincipal(identity);
    }

    private static ClaimsPrincipal CreateAnonymousUser()
        => new(new ClaimsIdentity());

    /// <summary>
    /// Minimal ServerCallContext for unit testing gRPC services.
    /// </summary>
    private sealed class TestServerCallContext : ServerCallContext, IDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        public void Dispose() => _cts.Dispose();
        protected override string MethodCore => "/honua.v1.FeatureService/QueryFeatures";
        protected override string HostCore => "localhost";
        protected override string PeerCore => "127.0.0.1";
        protected override DateTime DeadlineCore => DateTime.UtcNow.AddMinutes(5);
        protected override Metadata RequestHeadersCore => new();
        protected override CancellationToken CancellationTokenCore => _cts.Token;
        protected override Metadata ResponseTrailersCore => new();
        protected override Status StatusCore { get; set; }
        protected override WriteOptions? WriteOptionsCore { get; set; }
        protected override AuthContext AuthContextCore => new(null, new Dictionary<string, List<AuthProperty>>());

        protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) =>
            throw new NotImplementedException();

        protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) =>
            Task.CompletedTask;
    }

    /// <summary>
    /// Test double for IServerStreamWriter that collects written pages.
    /// </summary>
    private sealed class TestServerStreamWriter<T> : IServerStreamWriter<T>
    {
        public List<T> Pages { get; } = new();
        public WriteOptions? WriteOptions { get; set; }

        public Task WriteAsync(T message)
        {
            Pages.Add(message);
            return Task.CompletedTask;
        }

        public Task WriteAsync(T message, CancellationToken cancellationToken)
        {
            Pages.Add(message);
            return Task.CompletedTask;
        }
    }
}

/// <summary>
/// Provides async enumerable conversion for test data.
/// </summary>
internal static class AsyncEnumerableExtensions
{
    public static async IAsyncEnumerable<T> ToAsyncEnumerable<T>(this IEnumerable<T> source)
    {
        foreach (var item in source)
        {
            yield return item;
        }
        await Task.CompletedTask;
    }
}
