// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using FluentAssertions;
using Honua.Core.Features.Import.Domain;
using Honua.TestKit.Attributes;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.Import;

public sealed partial class StreamingFileImportStagingTableTests
{
    // Replace into a target that does not exist yet keeps the shared swap. It must not take the
    // create-only promotion or claim key and sequence names an earlier, renamed import still owns.
    [IntegrationTest]
    public async Task ImportFileAsync_FirstReplaceWhileRenamedImportOwnsTargetKey_PublishesTarget()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync("first_replace_key");
        try
        {
            await EnsureImportFunctionsAsync();
            await SeedOverwriteGuardTargetAsync(schema);
            await using (var connection = await fixture.DataSource.OpenConnectionAsync())
            await using (var rename = new NpgsqlCommand(
                $"ALTER TABLE \"{schema}\".\"{OverwriteGuardPhysicalTable}\" RENAME TO \"overwrite_guard_2024\"", connection))
            {
                await rename.ExecuteNonQueryAsync();
            }

            var service = CreateStreamingService(new TestConnectionProvider(fixture.DataSource, schema), schema);
            await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(PointGeoJson));
            var result = await service.ImportFileAsync(new ImportRequest
            {
                FileStream = stream,
                FileName = "points.geojson",
                TableName = OverwriteGuardLogicalTable,
                TargetSchema = schema,
                SourceSrid = 4326,
                TargetSrid = 4326,
                LoadMode = ImportLoadMode.Replace,
                OverwriteExisting = true,
            });

            result.Success.Should().BeTrue(result.ErrorMessage);
            (await TableExistsAsync(schema, OverwriteGuardPhysicalTable)).Should().BeTrue();
            (await TableExistsAsync(schema, "overwrite_guard_2024")).Should().BeTrue();
            (await TableExistsAsync(schema, OverwriteGuardPhysicalTable + "__staging")).Should().BeFalse();
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    // Component validation is shared by every load mode: a replace with a malformed polygon hole
    // is refused before promotion and the live table keeps its rows.
    [IntegrationTest]
    public async Task ImportFileAsync_ReplaceWithMalformedPolygonHole_LeavesLiveRows()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync("replace_components");
        try
        {
            await EnsureImportFunctionsAsync();
            await SeedOverwriteGuardTargetAsync(schema);
            await MarkImporterOwnedAsync(schema, OverwriteGuardPhysicalTable);
            var before = await ReadOverwriteGuardRowsAsync(schema);
            const string input = """{"type":"FeatureCollection","features":[{"type":"Feature","geometry":{"type":"Polygon","coordinates":[[[0,0],[4,0],[4,4],[0,0]],[[1,1],[2,2]]]},"properties":{}}]}""";
            await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(input));
            var service = CreateStreamingService(new TestConnectionProvider(fixture.DataSource, schema), schema);

            var result = await service.ImportFileAsync(new ImportRequest
            {
                FileStream = stream,
                FileName = "features.geojson",
                TableName = OverwriteGuardLogicalTable,
                TargetSchema = schema,
                SourceSrid = 4326,
                TargetSrid = 4326,
                LoadMode = ImportLoadMode.Replace,
                OverwriteExisting = true,
            });

            result.Success.Should().BeFalse();
            (await ReadOverwriteGuardRowsAsync(schema)).Should().Equal(before);
            (await StagingRelationsAsync(schema)).Should().BeEmpty();
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }
}
