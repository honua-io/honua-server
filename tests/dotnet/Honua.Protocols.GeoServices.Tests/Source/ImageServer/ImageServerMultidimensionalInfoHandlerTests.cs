// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Raster.Multidimensional.Abstractions;
using Honua.Core.Features.Raster.Multidimensional.Domain;
using Honua.Protocols.GeoServices.ImageServer.Handlers;
using Honua.Protocols.GeoServices.ImageServer.Models;
using Honua.Protocols.GeoServices.ImageServer.Services;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.ImageServer;

/// <summary>
/// Tests for the ImageServer multidimensionalInfo operation.
/// </summary>
[Protocol(TestProtocols.ImageServer)]
public class ImageServerMultidimensionalInfoHandlerTests
{
    private readonly TestMetadataV2GraphProvider _graphProvider = BuildGraphWithLayer(1);
    private readonly IMultidimensionalCoverageStore _store = Substitute.For<IMultidimensionalCoverageStore>();

    private ImageServerMultidimensionalInfoHandler CreateHandler()
        => new(
            _graphProvider,
            new ImageServerMultidimensionalInfoBuilder(_store),
            NullLogger<ImageServerMultidimensionalInfoHandler>.Instance);

    [UnitTest]
    [Operation(Operations.GetServiceInfo)]
    public async Task GetMultidimensionalInfoAsync_LayerNotFound_ReturnsNotFound()
    {
        var handler = CreateHandler();
        var context = CreateImageServerContext();

        var result = await handler.GetMultidimensionalInfoAsync(context, 99);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [UnitTest]
    [Operation(Operations.GetServiceInfo)]
    public async Task GetMultidimensionalInfoAsync_NoCoverage_ReturnsEmptyVariables()
    {
        _store.ListByLayerAsync(1, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<MultidimensionalCoverageRegistration>());

        var handler = CreateHandler();
        var context = CreateImageServerContext();

        var result = await handler.GetMultidimensionalInfoAsync(context, 1);

        var json = result as JsonHttpResult<MultidimensionalInfoResponse>;
        json.Should().NotBeNull();
        json!.Value!.MultidimensionalInfo.Variables.Should().BeEmpty();
    }

    [UnitTest]
    [Operation(Operations.GetServiceInfo)]
    public async Task GetMultidimensionalInfoAsync_RegistrationWithoutMetadata_ReturnsEmptyVariables()
    {
        _store.ListByLayerAsync(1, Arg.Any<CancellationToken>())
            .Returns([CreateRegistration(metadata: null)]);

        var handler = CreateHandler();
        var context = CreateImageServerContext();

        var result = await handler.GetMultidimensionalInfoAsync(context, 1);

        var json = result as JsonHttpResult<MultidimensionalInfoResponse>;
        json.Should().NotBeNull();
        json!.Value!.MultidimensionalInfo.Variables.Should().BeEmpty();
    }

    [UnitTest]
    [Operation(Operations.GetServiceInfo)]
    public async Task GetMultidimensionalInfoAsync_MultidimensionalCoverage_ReturnsVariablesWithDimensions()
    {
        _store.ListByLayerAsync(1, Arg.Any<CancellationToken>())
            .Returns([CreateRegistration(metadata: CreateTemperatureMetadata())]);

        var handler = CreateHandler();
        var context = CreateImageServerContext();

        var result = await handler.GetMultidimensionalInfoAsync(context, 1);

        var json = result as JsonHttpResult<MultidimensionalInfoResponse>;
        json.Should().NotBeNull();

        var variables = json!.Value!.MultidimensionalInfo.Variables;
        variables.Should().ContainSingle();

        var variable = variables[0];
        variable.Name.Should().Be("temperature");
        variable.Unit.Should().Be("K");
        variable.Description.Should().Be("Air Temperature");

        variable.Dimensions.Should().HaveCount(3);

        var timeDimension = variable.Dimensions.Should().ContainSingle(d => d.Name == "StdTime").Subject;
        timeDimension.Unit.Should().Be("ISO8601");
        timeDimension.DimensionSize.Should().Be(2);
        timeDimension.Extent.Should().HaveCount(2);

        var verticalDimension = variable.Dimensions.Should().ContainSingle(d => d.Name == "StdZ").Subject;
        verticalDimension.Unit.Should().Be("Pa");
        verticalDimension.Extent.Should().Equal(1000.0, 50000.0);

        // The horizontal axis stays under its source name with size only.
        variable.Dimensions.Should().ContainSingle(d => d.Name == "x")
            .Which.DimensionSize.Should().Be(360);
    }

    private static MultidimensionalCoverageMetadata CreateTemperatureMetadata() => new()
    {
        Format = MultidimensionalCoverageFormat.NetCdf4,
        Srid = 4326,
        Extent = new RasterExtent { XMin = -180, YMin = -90, XMax = 180, YMax = 90, Srid = 4326 },
        Resolution = (1.0, 1.0),
        Temporal = new TemporalExtent(
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddDays(1),
            StepCount: 2),
        Vertical = new VerticalExtent(1000.0, 50000.0, StepCount: 5, Units: "Pa"),
        Variables =
        [
            new MultidimensionalCoverageVariable(
                Name: "temperature",
                DataType: "float32",
                Dimensions:
                [
                    new MultidimensionalCoverageDimension("time", 2),
                    new MultidimensionalCoverageDimension("level", 5),
                    new MultidimensionalCoverageDimension("x", 360)
                ],
                ChunkLayout: null,
                Units: "K",
                LongName: "Air Temperature",
                StandardName: "air_temperature",
                NoData: -9999)
        ]
    };

    private static MultidimensionalCoverageRegistration CreateRegistration(
        MultidimensionalCoverageMetadata? metadata)
    {
        return new MultidimensionalCoverageRegistration
        {
            Id = 7,
            LayerId = 1,
            Name = "cube",
            Format = MultidimensionalCoverageFormat.NetCdf4,
            Provider = CloudStorageProvider.AwsS3,
            Bucket = "bucket",
            ObjectKey = "cube.nc",
            Variables = Array.Empty<string>(),
            Metadata = metadata,
            MetadataScannedAt = metadata is null ? null : DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    private static DefaultHttpContext CreateImageServerContext()
    {
        var services = new ServiceCollection();
        services.AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(NullLoggerFactory.Instance);

        var context = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider()
        };
        context.Request.Path = "/rest/services/1/ImageServer/multidimensionalInfo";
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static TestMetadataV2GraphProvider BuildGraphWithLayer(int layerIndex)
        => new TestMetadataV2GraphBuilder()
            .AddResource($"resource-{layerIndex}", "test-layer", MetadataV2ResourceType.RasterDataset)
            .AddService($"service-{layerIndex}", $"image-svc-{layerIndex}", protocols: [ServiceProtocols.ImageServer])
            .AddPublication(
                $"publication-{layerIndex}",
                $"service-{layerIndex}",
                $"resource-{layerIndex}",
                layerIndex: layerIndex,
                serviceLocalId: "test-layer",
                publicationType: MetadataV2PublicationType.EsriImageLayer)
            .BuildProvider();
}

/// <summary>
/// Unit tests for <see cref="ImageServerSlicesBuilder"/> (#1445), which derives the Esri
/// slices document from a multidimensional info document.
/// </summary>
[Protocol(TestProtocols.ImageServer)]
public class ImageServerSlicesBuilderTests
{
    [UnitTest]
    [Operation(Operations.GetServiceInfo)]
    public void Build_NoVariables_ReturnsEmpty()
    {
        var slices = ImageServerSlicesBuilder.Build(new ImageServerMultidimensionalInfo());

        slices.Should().BeEmpty();
    }

    [UnitTest]
    [Operation(Operations.GetServiceInfo)]
    public void Build_DimensionsWithoutEnumerableValues_ReturnsEmpty()
    {
        // Dimensions known only by extent (no enumerable values) cannot be sliced.
        var info = new ImageServerMultidimensionalInfo
        {
            Variables =
            [
                new ImageServerMultidimensionalVariable
                {
                    Name = "temperature",
                    Dimensions =
                    [
                        new ImageServerMultidimensionalDimension
                        {
                            Name = "StdTime",
                            Extent = [0, 86_400_000],
                            DimensionSize = 2
                        }
                    ]
                }
            ]
        };

        ImageServerSlicesBuilder.Build(info).Should().BeEmpty();
    }

    [UnitTest]
    [Operation(Operations.GetServiceInfo)]
    public void Build_EnumerableValues_ProducesCartesianProductSlices()
    {
        var info = new ImageServerMultidimensionalInfo
        {
            Variables =
            [
                new ImageServerMultidimensionalVariable
                {
                    Name = "temperature",
                    Dimensions =
                    [
                        new ImageServerMultidimensionalDimension
                        {
                            Name = "StdTime",
                            Values = [0, 86_400_000],
                            DimensionSize = 2
                        },
                        new ImageServerMultidimensionalDimension
                        {
                            Name = "StdZ",
                            Values = [1000],
                            DimensionSize = 1
                        }
                    ]
                }
            ]
        };

        var slices = ImageServerSlicesBuilder.Build(info);

        // 2 time values x 1 depth value = 2 slices, each pinning both dimensions.
        slices.Should().HaveCount(2);
        slices[0].SliceId.Should().Be(0);
        slices[1].SliceId.Should().Be(1);

        slices[0].MultidimensionalDefinition.Should().HaveCount(2);
        slices[0].MultidimensionalDefinition.Should()
            .ContainSingle(d => d.DimensionName == "StdTime" && d.VariableName == "temperature")
            .Which.Values.Should().Equal(0);
        slices[0].MultidimensionalDefinition.Should()
            .ContainSingle(d => d.DimensionName == "StdZ")
            .Which.Values.Should().Equal(1000);

        slices[1].MultidimensionalDefinition.Should()
            .ContainSingle(d => d.DimensionName == "StdTime")
            .Which.Values.Should().Equal(86_400_000);
    }
}
