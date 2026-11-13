// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Images;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Crs;
using Honua.Db.Postgres.Features.FeatureStore.Services;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Honua.Db.Postgres.Tests.Features.Infrastructure.Transforms;

[Collection("Database")]
[Protocol(ProtocolNames.TestQuality)]
[Operation(Operations.Project)]
public sealed class DatumGridProvisioningTests : IAsyncLifetime
{
    private readonly IDatumTransformationCatalog _catalog = GeoServicesDatumTransformationCatalog.Create();
    private IFutureDockerImage? _image;
    private PostgreSqlContainer? _container;
    private NpgsqlDataSource? _source;

    public async Task InitializeAsync()
    {
        _image = new ImageFromDockerfileBuilder()
            .WithDockerfileDirectory(FindGridContext())
            .WithBuildArgument("BASE_IMAGE", "pgrouting/pgrouting:17-3.5-3.7.3")
            .Build();
        await _image.CreateAsync();
        _container = new PostgreSqlBuilder().WithImage(_image)
            .WithDatabase("datum_grids").WithUsername("test").WithPassword("test")
            .Build();
        await _container.StartAsync();
        var network = await _container.ExecAsync(["printenv", "PROJ_NETWORK"]);
        network.ExitCode.Should().Be(0);
        network.Stdout.Trim().Should().Be("OFF");
        _source = NpgsqlDataSource.Create(_container.GetConnectionString());
        await using var command = _source.CreateCommand("CREATE EXTENSION IF NOT EXISTS postgis");
        await command.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        if (_source is not null)
        {
            await _source.DisposeAsync();
        }
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
        if (_image is not null)
        {
            await _image.DisposeAsync();
        }
    }

    [IntegrationTheory]
    [InlineData(-100, 40, "us_noaa_conus.tif")]
    [InlineData(-149.9, 61.2, "us_noaa_alaska.tif")]
    [InlineData(-79.4, 43.7, "ca_nrc_ntv2_0.tif")]
    public async Task AreaPipeline_DefaultImage_AppliesSelectedRegionalGrid(
        double longitude, double latitude, string grid)
    {
        _catalog.TryGetForEnvelope(4267, 4269, longitude, latitude, longitude, latitude, out var selection)
            .Should().BeTrue();
        selection!.RequiredGrids.Should().Contain(grid);
        var point = FormattableString.Invariant($"ST_SetSRID(ST_MakePoint({longitude}, {latitude}), 4267)");
        var expression = DatumTransformSql.BuildTransformExpression(point, 4269, selection);
        await using var command = _source!.CreateCommand($"SELECT ST_X({expression}), ST_Y({expression})");
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetDouble(0).Should().NotBe(longitude);
        reader.GetDouble(1).Should().NotBe(latitude);
    }

    [IntegrationTheory]
    [InlineData(4135, -157.8, 21.3)]
    [InlineData(4139, -66.1, 18.4)]
    public async Task AreaPipeline_IslandDatum_AppliesProvisionedGrid(
        int sourceSrid, double longitude, double latitude)
    {
        _catalog.TryGetForEnvelope(sourceSrid, 4269, longitude, latitude, longitude, latitude, out var selection)
            .Should().BeTrue();
        var point = FormattableString.Invariant($"ST_SetSRID(ST_MakePoint({longitude}, {latitude}), {sourceSrid})");
        var expression = DatumTransformSql.BuildTransformExpression(point, 4269, selection);
        await using var command = _source!.CreateCommand($"SELECT ST_X({expression}), ST_Y({expression})");
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetDouble(0).Should().NotBe(longitude);
        reader.GetDouble(1).Should().NotBe(latitude);
    }

    [IntegrationTest]
    public Task AreaPipeline_Alaska_AgreesWithIndependentProjReference() =>
        AssertProjReferenceAsync((-149.9, 61.2), (-149.902204940219, 61.199447934275));

    [IntegrationTest]
    public Task AreaPipeline_Canada_AgreesWithIndependentProjReference() =>
        AssertProjReferenceAsync((-79.4, 43.7), (-79.399801388899, 43.700056900002));

    [IntegrationTheory]
    [InlineData(-100, 40)]
    [InlineData(-149.9, 61.2)]
    [InlineData(-79.4, 43.7)]
    public async Task AreaPipeline_RegionalGridInverse_RestoresSourcePoint(double longitude, double latitude)
    {
        _catalog.TryGetForEnvelope(4267, 4269, longitude, latitude, longitude, latitude, out var forward)
            .Should().BeTrue();
        _catalog.TryGetForEnvelope(4269, 4267, longitude, latitude, longitude, latitude, out var inverse)
            .Should().BeTrue();
        var point = FormattableString.Invariant($"ST_SetSRID(ST_MakePoint({longitude}, {latitude}), 4267)");
        var transformed = DatumTransformSql.BuildTransformExpression(point, 4269, forward);
        var restored = DatumTransformSql.BuildTransformExpression(transformed, 4267, inverse);
        await using var command = _source!.CreateCommand($"SELECT ST_X({restored}), ST_Y({restored})");
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetDouble(0).Should().BeApproximately(longitude, 1e-8);
        reader.GetDouble(1).Should().BeApproximately(latitude, 1e-8);
    }

    private async Task AssertProjReferenceAsync(
        (double Longitude, double Latitude) input, (double Longitude, double Latitude) expected)
    {
        _catalog.TryGetForEnvelope(4267, 4269, input.Longitude, input.Latitude,
            input.Longitude, input.Latitude, out var selection).Should().BeTrue();
        var point = FormattableString.Invariant(
            $"ST_SetSRID(ST_MakePoint({input.Longitude}, {input.Latitude}), 4267)");
        var expression = DatumTransformSql.BuildTransformExpression(point, 4269, selection);
        var reference = FormattableString.Invariant(
            $"ST_SetSRID(ST_MakePoint({expected.Longitude}, {expected.Latitude}), 4269)");
        await using var command = _source!.CreateCommand(
            $"SELECT ST_Distance(({expression})::geography, ({reference})::geography)");
        var distance = (double)(await command.ExecuteScalarAsync())!;
        distance.Should().BeLessThan(0.01, "the selected pipeline must agree with the independent PROJ reference within one centimeter");
    }

    private static string FindGridContext()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var context = Path.Combine(directory.FullName, "docker", "proj-grids");
            if (File.Exists(Path.Combine(context, "Dockerfile")))
            {
                return context;
            }
        }
        throw new DirectoryNotFoundException("The default datum grid image build context was not found.");
    }
}
