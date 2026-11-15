// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using FluentAssertions;
using Honua.Core.Features.Import.Domain;
using Honua.Db.Postgres.Features.FileImport;
using Honua.TestKit.Attributes;
using Honua.TestKit.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Honua.Db.Postgres.Tests.Features.Import;

public sealed partial class StreamingFileImportStagingTableTests
{
    [IntegrationTheory]
    [InlineData("external")]
    [InlineData("importer")]
    public async Task ImportFileAsync_CreateOnlyExistingTarget_PreservesRowsAndIdentity(string owner)
    {
        var schema = await fixture.CreateIsolatedSchemaAsync("create_only_existing");
        try
        {
            await EnsureImportFunctionsAsync();
            await SeedOverwriteGuardTargetAsync(schema);
            if (owner == "importer")
            {
                await MarkImporterOwnedAsync(schema, OverwriteGuardPhysicalTable);
            }
            var identity = await ReadRelationIdAsync(schema, OverwriteGuardPhysicalTable);
            var before = await ReadOverwriteGuardRowsAsync(schema);

            var result = await ImportCreateOnlyAsync(schema);

            result.Success.Should().BeFalse();
            (await ReadRelationIdAsync(schema, OverwriteGuardPhysicalTable)).Should().Be(identity);
            (await ReadOverwriteGuardRowsAsync(schema)).Should().Equal(before);
            (await TableExistsAsync(schema, OverwriteGuardPhysicalTable + "__staging")).Should().BeFalse();
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task ImportFileAsync_CreateOnlyConcurrentImports_CommitsOneDataset()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync("create_only_concurrent");
        try
        {
            await EnsureImportFunctionsAsync();

            var results = await Task.WhenAll(ImportCreateOnlyAsync(schema), ImportCreateOnlyAsync(schema));

            results.Count(result => result.Success).Should().Be(1);
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var count = new NpgsqlCommand(
                $"SELECT count(*) FROM \"{schema}\".\"{OverwriteGuardPhysicalTable}\"", connection);
            (await count.ExecuteScalarAsync()).Should().Be(2L);
            (await TableExistsAsync(schema, OverwriteGuardPhysicalTable + "__staging")).Should().BeFalse();
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task ImportFileAsync_CreateOnlyTargetAppearsDuringLoad_PreservesExternalTable()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync("create_only_race");
        try
        {
            await EnsureImportFunctionsAsync();
            using var external = fixture.DataSource.OpenConnection();
            var created = false;
            var progress = new InlineImportProgress(value =>
            {
                if (created || value.BatchesCommitted == 0)
                {
                    return;
                }
                using var create = new NpgsqlCommand(
                    $"CREATE TABLE \"{schema}\".\"{OverwriteGuardPhysicalTable}\" (marker text); " +
                    $"INSERT INTO \"{schema}\".\"{OverwriteGuardPhysicalTable}\" VALUES ('external')", external);
                create.ExecuteNonQuery();
                created = true;
            });

            var result = await ImportCreateOnlyAsync(schema, progress);

            created.Should().BeTrue();
            result.Success.Should().BeFalse();
            await using var marker = new NpgsqlCommand(
                $"SELECT marker FROM \"{schema}\".\"{OverwriteGuardPhysicalTable}\"", external);
            (await marker.ExecuteScalarAsync()).Should().Be("external");
            (await StagingRelationsAsync(schema)).Should().BeEmpty();
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task ImportFileAsync_CreateOnlyReceiptFailure_RollsBackDataset()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync("create_only_receipt");
        try
        {
            await EnsureImportFunctionsAsync();
            var result = await ImportCreateOnlyAsync(schema, commitParticipant:
                (_, _) => throw new IOException("receipt persistence failed"));

            result.Success.Should().BeFalse();
            (await TableExistsAsync(schema, OverwriteGuardPhysicalTable)).Should().BeFalse();
            (await StagingRelationsAsync(schema)).Should().BeEmpty();
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task ImportFileAsync_CreateOnlyWithSkippedFeature_DiscardsIncompleteDataset()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync("create_only_partial");
        try
        {
            await EnsureImportFunctionsAsync();
            var service = CreateOnlyService(schema, ImportLimits.Default with
            {
                GeometryValidityMode = Honua.Core.Configuration.ValidationMode.Strict,
                SkipInvalidGeometry = true,
                ContinueOnError = true,
            });
            var path = Path.Join(AppContext.BaseDirectory, "Features", "Import", "Fixtures",
                "LoadMode", "mixed-validity-replace.geojson");
            await using var stream = File.OpenRead(path);
            var result = await service.ImportFileAsync(new ImportRequest
            {
                FileStream = stream,
                FileName = "mixed-validity.geojson",
                TableName = OverwriteGuardLogicalTable,
                TargetSchema = schema,
                SourceSrid = 4326,
                TargetSrid = 4326,
                LoadMode = ImportLoadMode.CreateOnly,
            });

            result.Success.Should().BeFalse();
            (await TableExistsAsync(schema, OverwriteGuardPhysicalTable)).Should().BeFalse();
            (await StagingRelationsAsync(schema)).Should().BeEmpty();
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    [IntegrationTest]
    public async Task ImportFileAsync_CreateOnlyEmptyDataset_RefusesWithoutCreatingTarget()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync("create_only_empty");
        try
        {
            await EnsureImportFunctionsAsync();
            await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(
                """{"type":"FeatureCollection","features":[]}"""));

            var result = await CreateOnlyService(schema).ImportFileAsync(new ImportRequest
            {
                FileStream = stream,
                FileName = "empty.geojson",
                TableName = OverwriteGuardLogicalTable,
                TargetSchema = schema,
                SourceSrid = 4326,
                TargetSrid = 4326,
                LoadMode = ImportLoadMode.CreateOnly,
            });

            result.Success.Should().BeFalse();
            (await TableExistsAsync(schema, OverwriteGuardPhysicalTable)).Should().BeFalse();
            (await StagingRelationsAsync(schema)).Should().BeEmpty();
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    private StreamingFileImportService CreateOnlyService(string schema, ImportLimits? limits = null)
    {
        var provider = new TestConnectionProvider(fixture.DataSource, schema);
        return new StreamingFileImportService(
            provider,
            new CrsDetectionService(provider, NullLogger<CrsDetectionService>.Instance),
            new TestFileFormatDetectionService(),
            new NoopPerformanceMonitor(),
            NullLogger<StreamingFileImportService>.Instance,
            limits ?? ImportLimits.Default with { BatchSize = 1 },
            schemaConfiguration: ImportTestSchemaConfiguration.WithOperational(schema));
    }

    private async Task<ImportResult> ImportCreateOnlyAsync(
        string schema, IProgress<ImportProgress>? progress = null, ImportCommitParticipant? commitParticipant = null)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(PointGeoJson));
        return await CreateOnlyService(schema).ImportFileAsync(new ImportRequest
        {
            FileStream = stream,
            FileName = "points.geojson",
            TableName = OverwriteGuardLogicalTable,
            TargetSchema = schema,
            SourceSrid = 4326,
            TargetSrid = 4326,
            LoadMode = ImportLoadMode.CreateOnly,
            CommitParticipant = commitParticipant,
        }, progress);
    }
}
