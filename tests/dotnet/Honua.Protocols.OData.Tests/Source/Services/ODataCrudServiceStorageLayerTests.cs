// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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
}
