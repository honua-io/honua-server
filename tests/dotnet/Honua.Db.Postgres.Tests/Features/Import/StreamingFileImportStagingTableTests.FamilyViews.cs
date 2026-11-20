// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Db.Postgres.Features.Infrastructure;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.Import;

public sealed partial class StreamingFileImportStagingTableTests
{
    private static readonly string[] _replaceProtectionMigrations =
    [
        "070_AddImportLoadModes.sql",
        "071_FixImportStagingTableNameLength.sql",
        "108_FixLongImportIndexNames.sql",
        "129_ProtectImportTableOwnership.sql",
        "145_ProtectImportDependencies.sql"
    ];

    [IntegrationTest]
    public async Task ImportFileAsync_ReplaceUnderPerTypeView_RecreatesViewOverNewRows()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync("family_view_replace");
        try
        {
            await EnsureImportFunctionsAsync();
            foreach (var file in _replaceProtectionMigrations)
            {
                await fixture.ApplyGlobalSeedSqlAsync(await File.ReadAllTextAsync(RepositoryPaths.Resolve("src", "Honua.Server", "Migrations", file)));
            }

            await SeedOverwriteGuardTargetAsync(schema);
            await MarkImporterOwnedAsync(schema, OverwriteGuardPhysicalTable);
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using (var create = new NpgsqlCommand(
                GeometryFamilyViews.CreateSql(
                    schema, OverwriteGuardPhysicalTable, new GeometryFamilyViews.FamilyFilter("Point", "geometry")), connection))
            {
                await create.ExecuteNonQueryAsync();
            }

            var result = await ImportOverwriteGuardFixtureAsync(schema, overwriteExisting: true);

            result.Success.Should().BeTrue(result.ErrorMessage);
            var viewName = GeometryFamilyViews.ViewName(OverwriteGuardPhysicalTable, "Point");
            (await ReadOverwriteGuardRowsAsync(schema, viewName)).Select(row => (row.Name, row.Code, row.Wkt))
                .Should().Equal(OverwriteGuardFixtureRows);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
            await EnsureImportFunctionsAsync();
        }
    }
}
