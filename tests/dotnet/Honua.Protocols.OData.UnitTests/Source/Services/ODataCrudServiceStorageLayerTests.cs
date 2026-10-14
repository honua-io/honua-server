// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.Core.Features.Edit;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Geometry.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Infrastructure.Caching;
using Honua.Infrastructure.Validation;
using Honua.Protocols.OData.Models;
using Honua.Protocols.OData.Services;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Honua.Protocols.OData.Tests.Services;

/// <summary>
/// The OData write surface addresses a layer by its service-local route id, while the feature
/// reader/writer boundary is keyed on the storage-layer handle. These tests pin that the CRUD
/// service reads and writes the handle its caller resolved, not the route id, so an aliased
/// publication (route id != storage handle) touches the rows of the publication the request
/// actually resolved.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Component", "OData")]
[Trait("Feature", "Crud")]
public sealed class ODataCrudServiceStorageLayerTests
{
    /// <summary>Service-local route id; also the storage handle of a different resource.</summary>
    private const int RouteLayerId = 3;

    /// <summary>Storage handle the addressed publication is bound to.</summary>
    private const int StorageLayerId = 7;

    private const long ObjectId = 42;

    [global::Xunit.Trait("Tier", "Fast")]
    [Fact]
    public async Task UpdateFeatureAsync_ReadsTheSuppliedStorageLayerNotTheRouteLayer()
    {
        var reader = Substitute.For<IFeatureReader>();
        var service = CreateService(reader);

        var result = await service.UpdateFeatureAsync(
            RouteLayerId,
            ObjectId,
            new ParsedFeaturePayload(),
            "https://example.test",
            ifMatch: null,
            ifNoneMatch: null,
            StorageLayerId,
            replace: false,
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        await reader.Received(1).GetAsync(StorageLayerId, ObjectId, Arg.Any<CancellationToken>());
        await reader.DidNotReceive().GetAsync(RouteLayerId, ObjectId, Arg.Any<CancellationToken>());
    }

    [global::Xunit.Trait("Tier", "Fast")]
    [Fact]
    public async Task DeleteFeatureAsync_ReadsTheSuppliedStorageLayerNotTheRouteLayer()
    {
        var reader = Substitute.For<IFeatureReader>();
        var service = CreateService(reader);

        var result = await service.DeleteFeatureAsync(
            RouteLayerId,
            ObjectId,
            ifMatch: null,
            ifNoneMatch: null,
            StorageLayerId,
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        await reader.Received(1).GetAsync(StorageLayerId, ObjectId, Arg.Any<CancellationToken>());
        await reader.DidNotReceive().GetAsync(RouteLayerId, ObjectId, Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task CreateFeatureAsync_UsesAuthorizedFeatureResourceInsteadOfGenericRasterSidecar()
    {
        var validator = Substitute.For<IResourceValidator>();
        validator.ValidateLayerV2Async(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ResourceValidationResult.Success(new MetadataV2Resource
            {
                Metadata = new MetadataV2ObjectMetadata { Id = "raster-sidecar" },
                Type = MetadataV2ResourceType.RasterDataset
            }));
        var featureResource = CreateResource() with
        {
            SchemaFields = [new MetadataV2Field { Name = "name", Type = MetadataV2FieldType.String, Editable = true }]
        };
        var adapter = Substitute.For<IEditParameterAdapter<ODataEditRequest>>();
        adapter.ConvertAsync(Arg.Any<ODataEditRequest>(), featureResource, Arg.Any<CancellationToken>())
            .Returns(EditAdapterResult.Failure("Conversion reached the authorized feature resource."));
        var dependencies = new ODataCrudDependencies(
            validator, Substitute.For<IFeatureReader>(), Substitute.For<IFeatureWriter>(),
            Substitute.For<IGeometryService>(), Substitute.For<ICrsRegistry>(), Substitute.For<IETagService>(),
            new FeatureMutationValidator(Substitute.For<IGeometryValidator>()), adapter,
            Substitute.For<IEditProcessor>());
        var service = new ODataCrudService(dependencies, NullLogger<ODataCrudLog>.Instance);

        var result = await service.CreateFeatureAsync(RouteLayerId,
            new ParsedFeaturePayload { Attributes = new Dictionary<string, object?> { ["name"] = "Canonical City" } },
            "https://example.test", StorageLayerId, featureResource, CancellationToken.None);

        result.ErrorMessage.Should().Be("Conversion reached the authorized feature resource.");
        await adapter.Received(1).ConvertAsync(Arg.Any<ODataEditRequest>(), featureResource, Arg.Any<CancellationToken>());
        await validator.DidNotReceive().ValidateLayerV2Async(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task CreateFeatureAsync_WhenReloadReturnsNull_AcknowledgesTheInsertedFeature()
    {
        var result = await CreateCommittedFeatureAsync(reader =>
            reader.GetAsync(Arg.Any<int>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<Feature?>(null)));

        result.IsSuccess.Should().BeTrue();
        result.StatusCode.Should().Be(201);
        result.Data.Should().NotBeNull();
        result.Data!["ObjectId"].Should().Be(77L);
        result.Data["name"].Should().Be("Canonical City");
        result.LocationHeader.Should().Contain("ObjectId=77");
    }

    [UnitTest]
    public async Task CreateFeatureAsync_WhenReadbackIsCancelled_AcknowledgesTheInsertedFeature()
    {
        var result = await CreateCommittedFeatureAsync(reader =>
            reader.GetAsync(Arg.Any<int>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
                .Returns(_ => Task.FromException<Feature?>(new OperationCanceledException())));

        result.IsSuccess.Should().BeTrue();
        result.StatusCode.Should().Be(201);
        result.Data.Should().NotBeNull();
        result.Data!["ObjectId"].Should().Be(77L);
        result.Data["name"].Should().Be("Canonical City");
    }

    [UnitTest]
    public async Task CreateFeatureAsync_WhenInsertFails_StaysAnError()
    {
        var reader = Substitute.For<IFeatureReader>();
        var service = CreateCommittedService(reader, FeatureEditResult.Success(
            createdCount: 0,
            updatedCount: 0,
            deletedCount: 0,
            createResults: ImmutableArray.Create(EditOperationResult.Failure("constraint"))));

        var result = await service.CreateFeatureAsync(
            RouteLayerId,
            new ParsedFeaturePayload { Attributes = new Dictionary<string, object?> { ["name"] = "Canonical City" } },
            "https://example.test",
            StorageLayerId,
            CreateNamedResource(),
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.StatusCode.Should().Be(500);
        result.ErrorMessage.Should().Be("An error occurred creating the feature");
        await reader.DidNotReceive().GetAsync(Arg.Any<int>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    private static async Task<ODataCrudResult<Dictionary<string, object?>>> CreateCommittedFeatureAsync(
        Action<IFeatureReader> configureReader)
    {
        var reader = Substitute.For<IFeatureReader>();
        configureReader(reader);
        var service = CreateCommittedService(reader, FeatureEditResult.Success(
            createdCount: 1,
            updatedCount: 0,
            deletedCount: 0,
            createResults: ImmutableArray.Create(EditOperationResult.Success(77))));
        return await service.CreateFeatureAsync(
            RouteLayerId,
            new ParsedFeaturePayload { Attributes = new Dictionary<string, object?> { ["name"] = "Canonical City" } },
            "https://example.test",
            StorageLayerId,
            CreateNamedResource(),
            CancellationToken.None);
    }

    private static ODataCrudService CreateCommittedService(IFeatureReader reader, FeatureEditResult editResult)
    {
        var adapter = Substitute.For<IEditParameterAdapter<ODataEditRequest>>();
        adapter.ProtocolName.Returns("OData");
        var editRequest = UnifiedEditRequest.WithCreates(ImmutableArray<EditFeature>.Empty);
        adapter.ConvertAsync(Arg.Any<ODataEditRequest>(), Arg.Any<MetadataV2Resource>(), Arg.Any<CancellationToken>())
            .Returns(EditAdapterResult.Success(editRequest, EditTransaction.CreateSimple("tx", "OData", editRequest)));

        // EditTransaction is a record struct. NSubstitute's Arg.Any<EditTransaction>()
        // formats a default value, and that value's uninitialized edit list throws.
        var processor = new AcceptingEditProcessor();

        var writer = new StubFeatureWriter(editResult);

        // ComputeETag(ReadOnlySpan<byte>) cannot be substituted. The create path
        // hashes the serialized payload with that overload after the insert commits.
        IETagService etag = new FixedETagService();

        var dependencies = new ODataCrudDependencies(
            Substitute.For<IResourceValidator>(),
            reader,
            writer,
            Substitute.For<IGeometryService>(),
            Substitute.For<ICrsRegistry>(),
            etag,
            new FeatureMutationValidator(Substitute.For<IGeometryValidator>()),
            adapter,
            processor);
        return new ODataCrudService(dependencies, NullLogger<ODataCrudLog>.Instance);
    }

    private static MetadataV2Resource CreateNamedResource()
        => CreateResource() with
        {
            SchemaFields = [new MetadataV2Field { Name = "name", Type = MetadataV2FieldType.String, Editable = true, Nullable = true }],
        };

    private static ODataCrudService CreateService(IFeatureReader reader)
    {
        var resourceValidator = Substitute.For<IResourceValidator>();
        resourceValidator
            .ValidateLayerV2Async(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ResourceValidationResult.Success(CreateResource()));

        // The target row is absent from every layer, so both operations stop at the
        // existence check. That keeps the assertion about which layer was read.
        reader.GetAsync(Arg.Any<int>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Feature?>(null));

        var dependencies = new ODataCrudDependencies(
            resourceValidator,
            reader,
            Substitute.For<IFeatureWriter>(),
            Substitute.For<IGeometryService>(),
            Substitute.For<ICrsRegistry>(),
            Substitute.For<IETagService>(),
            new FeatureMutationValidator(Substitute.For<IGeometryValidator>()),
            Substitute.For<IEditParameterAdapter<ODataEditRequest>>(),
            Substitute.For<IEditProcessor>());

        return new ODataCrudService(dependencies, NullLogger<ODataCrudLog>.Instance);
    }

    private static MetadataV2Resource CreateResource()
        => new()
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "resource.aliased", Name = "aliased" },
            Type = MetadataV2ResourceType.FeatureDataset,
            Spatial = new MetadataV2ResourceSpatial
            {
                GeometryType = MetadataV2GeometryType.Point,
                SpatialReference = MetadataV2SpatialReference.Wgs84
            },
            SchemaFields =
            [
                new MetadataV2Field
                {
                    Name = "objectid",
                    Type = MetadataV2FieldType.Integer,
                    Nullable = false,
                    SemanticRoles = ["id.primary"]
                }
            ]
        };

    private sealed class FixedETagService : IETagService
    {
        public string ComputeETag<T>(T obj, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> jsonTypeInfo)
            => "\"etag\"";

        public string ComputeETag(ReadOnlySpan<byte> data) => "\"etag\"";

        public string ComputeETag(string content) => "\"etag\"";

        public bool IsModified(string? ifNoneMatch, string currentETag) => true;

        public bool MatchesPrecondition(string? ifMatch, string currentETag) => true;

        public void SetCacheHeaders(Microsoft.AspNetCore.Http.HttpResponse response, string etag, DateTimeOffset? lastModified = null)
        {
        }
    }

    private sealed class StubFeatureWriter(FeatureEditResult result) : IFeatureWriter
    {
        public Task<FeatureEditResult> ApplyEditsAsync(
            int layerId,
            FeatureEditBatch editBatch,
            CancellationToken cancellationToken = default)
            => Task.FromResult(result);

        public Task<Feature> CreateAsync(int layerId, Feature feature, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<Feature> UpdateAsync(int layerId, Feature feature, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<bool> DeleteAsync(int layerId, long featureId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class AcceptingEditProcessor : IEditProcessor
    {
        public EditValidationResult ValidateEdit(UnifiedEditRequest editRequest, MetadataV2Resource resource)
            => EditValidationResult.Success();

        public UnifiedEditRequest OptimizeEdit(UnifiedEditRequest editRequest, MetadataV2Resource resource)
            => editRequest;

        public FeatureEditBatch ToFeatureEditBatch(UnifiedEditRequest editRequest, MetadataV2Resource resource)
            => FeatureEditBatch.Create(creates: ImmutableArray.Create(Feature.Create(0, null)));

        public TransactionValidationResult ValidateTransaction(EditTransaction transaction, MetadataV2Resource resource)
            => TransactionValidationResult.Success();

        public EditExecutionStrategy DetermineExecutionStrategy(UnifiedEditRequest editRequest, MetadataV2Resource resource)
            => new();

        public Task<EditPerformanceEstimate> EstimatePerformanceAsync(
            UnifiedEditRequest editRequest,
            MetadataV2Resource resource,
            CancellationToken cancellationToken)
            => Task.FromResult(new EditPerformanceEstimate());
    }
}
