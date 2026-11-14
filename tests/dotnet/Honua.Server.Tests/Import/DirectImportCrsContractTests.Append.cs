// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using System.Text;

namespace Honua.Server.Tests.Import;

public sealed partial class DirectImportCrsContractTests
{
    [IntegrationTheory]
    [InlineData(ImportLoadMode.Append, null)]
    [InlineData(ImportLoadMode.Upsert, null)]
    [InlineData(ImportLoadMode.Append, 2193)]
    [InlineData(ImportLoadMode.Upsert, 2193)]
    [Endpoint("POST /api/v1/admin/import/upload")]
    public async Task ImportFile_ExistingTarget_UsesStoredCrsUnlessExplicitlyOverridden(ImportLoadMode mode, int? targetSrid)
    {
        var tableName = NewImportFields()["TableName"];
        var seed = await ImportCsvAsync(tableName, "SRID=4326;POINT (0 0)", new CsvImportOptions(ImportLoadMode.Replace, 4326));
        seed.Success.Should().BeTrue(seed.ErrorMessage);
        _importedTables.Add((seed.Schema!, seed.PhysicalTableName!));

        var result = await ImportCsvAsync(tableName, ProjectedPoint, new CsvImportOptions(mode, targetSrid));
        result.Success.Should().Be(!targetSrid.HasValue, result.ErrorMessage);
        if (targetSrid.HasValue)
        {
            result.ErrorMessage.Should().Contain("storage CRS does not match");
        }
        else
        {
            result.SourceSrid.Should().Be(2193);
            result.StoredSrid.Should().Be(4326);
            result.Reprojected.Should().BeTrue();
        }

        await using var connection = await _fixture.Postgres.GetConnectionAsync();
        await using var query = new NpgsqlCommand($"""
            SELECT count(*)::int, count(*) FILTER (WHERE
                ST_SRID(geometry) = 4326 AND ST_DWithin(geometry,
                    ST_Transform(ST_GeomFromEWKT(@projected), 4326), 0.00000001))::int
            FROM {QualifiedTable(seed.Schema!, seed.PhysicalTableName!)}
            """, connection);
        query.Parameters.AddWithValue("projected", ProjectedPoint);
        await using var reader = await query.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetInt32(0).Should().Be(!targetSrid.HasValue && mode == ImportLoadMode.Append ? 2 : 1);
        reader.GetInt32(1).Should().Be(targetSrid.HasValue ? 0 : 1);
    }

    [IntegrationTheory]
    [InlineData(ImportLoadMode.Append)]
    [InlineData(ImportLoadMode.Upsert)]
    [Endpoint("POST /api/v1/admin/import/upload")]
    public async Task ImportFile_NewAppendTargetWithoutOverride_PreservesSourceCrs(ImportLoadMode mode)
    {
        var result = await ImportCsvAsync(NewImportFields()["TableName"], ProjectedPoint, new CsvImportOptions(mode, null));
        result.Success.Should().BeTrue(result.ErrorMessage);
        _importedTables.Add((result.Schema!, result.PhysicalTableName!));
        result.StoredSrid.Should().Be(2193);
        result.Reprojected.Should().BeFalse();
        await using var connection = await _fixture.Postgres.GetConnectionAsync();
        await using var query = new NpgsqlCommand($"SELECT ST_SRID(geometry) FROM {QualifiedTable(result.Schema!, result.PhysicalTableName!)}", connection);
        (await query.ExecuteScalarAsync()).Should().Be(2193);
    }

    private sealed record CsvImportOptions(ImportLoadMode Mode, int? TargetSrid);

    private async Task<ImportResult> ImportCsvAsync(string tableName, string geometry, CsvImportOptions options)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes($"geometry,name\n\"{geometry}\",sample\n"));
        await using var scope = _fixture.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IFileImportService>().ImportFileAsync(new ImportRequest
        {
            FileStream = stream,
            FileName = "point.csv",
            TableName = tableName,
            LoadMode = options.Mode,
            KeyColumns = options.Mode == ImportLoadMode.Upsert ? ["name"] : [],
            TargetSrid = options.TargetSrid
        });
    }
}
