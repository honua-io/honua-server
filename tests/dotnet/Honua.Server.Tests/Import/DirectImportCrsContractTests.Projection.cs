// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Import.Domain;
using Honua.TestKit.Attributes;
using Npgsql;

namespace Honua.Server.Tests.Import;

public sealed partial class DirectImportCrsContractTests
{
    [IntegrationTheory]
    [InlineData(ImportLoadMode.Append)]
    [InlineData(ImportLoadMode.Upsert)]
    [Endpoint("POST /api/v1/admin/import/upload")]
    public async Task ImportFile_InheritedUnsupportedProjection_RefusesBeforeLoadingAnyRows(ImportLoadMode mode)
    {
        await using var connection = await _fixture.Postgres.GetConnectionAsync();
        await using var register = new NpgsqlCommand("""
            INSERT INTO spatial_ref_sys (srid, auth_name, auth_srid, srtext, proj4text)
            VALUES (910001, 'HONUA_TEST', 910001, 'unsupported test projection', '+proj=honua_invalid_projection')
            """, connection);
        await register.ExecuteNonQueryAsync();
        try
        {
            var tableName = NewImportFields()["TableName"];
            var seed = await ImportCsvAsync(tableName, "SRID=910001;POINT (0 0)",
                new AppendImportOptions(ImportLoadMode.Replace, 910001));
            seed.Success.Should().BeTrue(seed.ErrorMessage);
            _importedTables.Add((seed.Schema!, seed.PhysicalTableName!));

            var result = await ImportCsvAsync(tableName, "SRID=4326;POINT (0 0)",
                new AppendImportOptions(mode, null) { IncludeNullGeometry = true });
            result.Success.Should().BeFalse();
            result.ErrorCode.Should().Be(ImportValidationErrorCodes.ProjectionUnsupported);
            result.ValidationErrors.Should().ContainSingle(issue =>
                issue.Code == ImportValidationErrorCodes.ProjectionUnsupported);
            await using var count = new NpgsqlCommand(
                $"SELECT count(*)::int FROM {QualifiedTable(seed.Schema!, seed.PhysicalTableName!)}", connection);
            (await count.ExecuteScalarAsync()).Should().Be(1);
        }
        finally
        {
            await using var cleanup = new NpgsqlCommand("DELETE FROM spatial_ref_sys WHERE srid = 910001", connection);
            await cleanup.ExecuteNonQueryAsync();
        }
    }
}
